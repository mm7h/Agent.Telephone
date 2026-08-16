using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Abstractions.Persistence;
using Agent.Telephone.Common.Enums;
using Agent.Telephone.Helpers;
using Agent.Telephone.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace Agent.Telephone.Common.Contexts
{
    internal sealed class DeviceContext : IDisposable
    {
        private readonly SIPTransport _sipTransport;
        private readonly ILogger _logger;
        private readonly object _callSessionLock = new();
        private readonly object _registrationLock = new();
        private ActiveCallContext? _activeCall;
        private SIPServerUserAgent? _pendingServerUserAgent;
        private RegistrationBinding? _registration;
        private RegistrationState _registrationState;
        private CallState _callState;
        private bool _callbackActive;
        private int _backgroundReplyCount;
        private int _persistenceCount;
        private string? _historyPersistenceCallId;

        public DeviceContext(
            SIPTransport sipTransport,
            SIPRequest sipRequest,
            SIPURI contact,
            int expiresSeconds,
            List<AssistantConfig> availableAssistants,
            ILogger? logger = null)
        {
            this._sipTransport = sipTransport;
            this._logger = logger ?? NullLogger.Instance;

            this.DeviceId = sipRequest.GetDeviceId();
            this.AvailableAssistants = availableAssistants.ToDictionary(i => i.DialingNumber).AsReadOnly();
            this.AudioInPacket = new AudioInPacket();
            this.LoginTime = DateTimeOffset.Now;
            this.LastActivityTime = this.LoginTime;
            this.UpdateRegistration(sipRequest, contact, expiresSeconds);
        }

        public DeviceContext(
            SIPTransport sipTransport,
            DeviceRegistrationRecord registration,
            List<AssistantConfig> availableAssistants,
            ILogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(registration);

            this._sipTransport = sipTransport;
            this._logger = logger ?? NullLogger.Instance;
            this.DeviceId = registration.DeviceId;
            this.AvailableAssistants = availableAssistants.ToDictionary(i => i.DialingNumber).AsReadOnly();
            this.AudioInPacket = new AudioInPacket();
            this.LoginTime = DateTimeOffset.Now;
            this.LastActivityTime = registration.RefreshedAt;
            this._registration = new RegistrationBinding(
                SIPURI.ParseSIPURI(registration.Aor),
                SIPURI.ParseSIPURI(registration.Contact),
                SIPEndPoint.Empty,
                SIPEndPoint.Empty,
                registration.RegisteredAt,
                registration.RefreshedAt,
                registration.ExpiresAt);
            this._registrationState = RegistrationState.Registered;
        }

        public string DeviceId { get; }
        public DateTimeOffset LoginTime { get; }
        public DateTimeOffset LastActivityTime { get; private set; }
        public RegistrationState RegistrationState
        {
            get
            {
                lock (this._registrationLock)
                {
                    this.ExpireRegistrationIfNeeded(DateTimeOffset.Now);
                    return this._registrationState;
                }
            }
        }
        public CallState CallState
        {
            get
            {
                lock (this._callSessionLock)
                {
                    return this._callState;
                }
            }
        }
        public RegistrationBinding? Registration
        {
            get
            {
                lock (this._registrationLock)
                {
                    this.ExpireRegistrationIfNeeded(DateTimeOffset.Now);
                    return this._registration;
                }
            }
        }
        public SIPEndPoint LocalEndPoint => this.Registration?.LocalEndPoint ?? SIPEndPoint.Empty;
        public SIPEndPoint RemoteEndPoint => this.Registration?.RemoteEndPoint ?? SIPEndPoint.Empty;
        public AudioInPacket AudioInPacket { get; }
        public ActiveCallContext? ActiveCall
        {
            get
            {
                lock (this._callSessionLock)
                {
                    return this._activeCall;
                }
            }
        }
        public bool IsCallOccupied
        {
            get
            {
                lock (this._callSessionLock)
                {
                    return this._activeCall is not null || this._callbackActive || this._backgroundReplyCount > 0 || this._persistenceCount > 0;
                }
            }
        }
        public IReadOnlyDictionary<string, AssistantConfig> AvailableAssistants { get; }

        public bool IsRegistered()
        {
            lock (this._registrationLock)
            {
                this.ExpireRegistrationIfNeeded(DateTimeOffset.Now);
                return this._registrationState == RegistrationState.Registered;
            }
        }

        public bool TryGetActiveRegistration(out RegistrationBinding? registration)
        {
            lock (this._registrationLock)
            {
                this.ExpireRegistrationIfNeeded(DateTimeOffset.Now);
                registration = this._registrationState == RegistrationState.Registered
                    ? this._registration
                    : null;
                return registration is not null;
            }
        }

        public void UpdateRegistration(SIPRequest sipRequest, SIPURI contact, int expiresSeconds)
        {
            DateTimeOffset now = DateTimeOffset.Now;
            lock (this._registrationLock)
            {
                this._registrationState = RegistrationState.Registering;
                DateTimeOffset registeredAt = this._registration?.RegisteredAt ?? now;
                this._registration = new RegistrationBinding(
                    sipRequest.GetCallerAor(),
                    contact,
                    sipRequest.LocalSIPEndPoint,
                    sipRequest.RemoteSIPEndPoint,
                    registeredAt,
                    now,
                    now.AddSeconds(expiresSeconds));
                this._registrationState = RegistrationState.Registered;
                this.LastActivityTime = now;
            }
        }

        public void Unregister()
        {
            lock (this._registrationLock)
            {
                this._registration = null;
                this._registrationState = RegistrationState.Offline;
                this.LastActivityTime = DateTimeOffset.Now;
            }
        }

        public bool TryInitializeCallSession(SIPRequest sipRequest, out ActiveCallContext? activeCall)
        {
            lock (this._callSessionLock)
            {
                if (this._activeCall is not null || this._callbackActive || this._backgroundReplyCount > 0 || this._persistenceCount > 0)
                {
                    activeCall = null;
                    return false;
                }

                activeCall = new ActiveCallContext(this._sipTransport, sipRequest, this);
                this._activeCall = activeCall;
                this._callState = CallState.PreparingAgent;

                try
                {
                    this._pendingServerUserAgent = activeCall.UserAgent.AcceptCall(sipRequest);
                    activeCall.UserAgent.ServerCallCancelled += this.OnServerCallCancelled;
                    this._pendingServerUserAgent.Progress(
                        SIPResponseStatusCodesEnum.Ringing,
                        null!,
                        null!,
                        null!,
                        null!);
                    this.RefreshLastActivityTime();
                    return true;
                }
                catch
                {
                    activeCall.UserAgent.ServerCallCancelled -= this.OnServerCallCancelled;
                    this._pendingServerUserAgent = null;
                    this._activeCall = null;
                    this._callState = CallState.Failed;
                    activeCall.Dispose();
                    throw;
                }
            }
        }

        public SIPServerUserAgent? TakePendingServerUserAgent(ActiveCallContext activeCall)
        {
            lock (this._callSessionLock)
            {
                if (!ReferenceEquals(this._activeCall, activeCall))
                {
                    return null;
                }

                SIPServerUserAgent? serverUserAgent = this._pendingServerUserAgent;
                this._pendingServerUserAgent = null;
                return serverUserAgent;
            }
        }

        public void RejectPendingCall(ActiveCallContext activeCall, SIPResponseStatusCodesEnum status, string reason)
        {
            SIPServerUserAgent? serverUserAgent;
            lock (this._callSessionLock)
            {
                if (!ReferenceEquals(this._activeCall, activeCall))
                {
                    return;
                }

                serverUserAgent = this._pendingServerUserAgent;
                this._pendingServerUserAgent = null;
                this._callState = CallState.Failed;
            }

            serverUserAgent?.Reject(status, reason);
            this.CloseCallSession(activeCall);
        }

        public bool TryBeginBackgroundReply(ActiveCallContext activeCall)
        {
            lock (this._callSessionLock)
            {
                if (!ReferenceEquals(this._activeCall, activeCall))
                {
                    return false;
                }

                this._backgroundReplyCount++;
                return true;
            }
        }

        public void EndBackgroundReply()
        {
            lock (this._callSessionLock)
            {
                if (this._backgroundReplyCount > 0)
                {
                    this._backgroundReplyCount--;
                }
            }
        }

        public bool TryRecordCompletedOnlineTurn(ActiveCallContext activeCall, OfflineDialogueTurn turn)
        {
            lock (this._callSessionLock)
            {
                if (!ReferenceEquals(this._activeCall, activeCall) ||
                    activeCall.CallToken.IsCancellationRequested ||
                    !activeCall.UserAgent.IsCallActive)
                {
                    return false;
                }

                activeCall.AIAgentContext.RecordCompletedOnlineTurn(turn);
                return true;
            }
        }

        public void MarkCallConnected(ActiveCallContext activeCall)
        {
            lock (this._callSessionLock)
            {
                if (ReferenceEquals(this._activeCall, activeCall))
                {
                    this._callState = CallState.AgentConnected;
                }
            }

            this.RefreshLastActivityTime();
        }

        public void MarkPlayingPrompt(ActiveCallContext activeCall)
        {
            lock (this._callSessionLock)
            {
                if (ReferenceEquals(this._activeCall, activeCall))
                {
                    this._callState = CallState.PlayingPrompt;
                }
            }
        }

        public void MarkCallEnding(ActiveCallContext activeCall)
        {
            lock (this._callSessionLock)
            {
                if (ReferenceEquals(this._activeCall, activeCall))
                {
                    this._callState = CallState.Ending;
                }
            }
        }

        public bool TryBeginCallback()
        {
            lock (this._callSessionLock)
            {
                if (this._activeCall is not null || this._callbackActive)
                {
                    return false;
                }

                this._callbackActive = true;
                this._callState = CallState.CallbackDialing;
                return true;
            }
        }

        public bool TryBeginCallback(ActiveCallContext activeCall)
        {
            lock (this._callSessionLock)
            {
                if (!ReferenceEquals(this._activeCall, activeCall) || this._callbackActive)
                {
                    return false;
                }

                this._callbackActive = true;
                this._callState = CallState.CallbackDialing;
                return true;
            }
        }

        public void MarkCallbackConnected()
        {
            lock (this._callSessionLock)
            {
                if (this._callbackActive)
                {
                    this._callState = CallState.CallbackConnected;
                }
            }
        }

        public bool TryAttachCallbackCallSession(
            string userAor,
            string assistantNumber,
            SIPUserAgent userAgent,
            SIPSorcery.Media.VoIPMediaSession mediaSession,
            out ActiveCallContext? activeCall)
        {
            lock (this._callSessionLock)
            {
                if (!this._callbackActive || this._activeCall is not null)
                {
                    activeCall = null;
                    return false;
                }

                activeCall = new ActiveCallContext(
                    this,
                    userAor,
                    assistantNumber,
                    userAgent,
                    mediaSession);
                this._activeCall = activeCall;
                this._callState = CallState.CallbackConnected;
                return true;
            }
        }

        public void EndCallback()
        {
            lock (this._callSessionLock)
            {
                if (!this._callbackActive)
                {
                    return;
                }

                this._callbackActive = false;
                this._callState = CallState.Idle;
            }
        }

        public void RefreshLastActivityTime()
        {
            lock (this._registrationLock)
            {
                this.LastActivityTime = DateTimeOffset.Now;
            }
        }

        public void CloseCallSession()
        {
            ActiveCallContext? activeCall;
            IOfflineDialogue? offlineDialogue = null;
            IDisposable? callUseLease = null;
            bool persistenceStarted = false;
            lock (this._callSessionLock)
            {
                activeCall = this._activeCall;
            }

            if (activeCall is not null)
            {
                persistenceStarted = this.TryBeginCallHistoryPersistence(activeCall, out offlineDialogue, out callUseLease);
            }

            lock (this._callSessionLock)
            {
                if (!ReferenceEquals(this._activeCall, activeCall))
                {
                    activeCall = null;
                }

                if (activeCall is not null)
                {
                    this._activeCall = null;
                    this._pendingServerUserAgent = null;
                    this._callState = this._callbackActive
                        ? CallState.Ending
                        : CallState.Idle;
                }
                else if (!this._callbackActive)
                {
                    this._callState = CallState.Idle;
                }
            }

            if (activeCall is null && persistenceStarted)
            {
                callUseLease?.Dispose();
                this.EndCallHistoryPersistence();
                return;
            }

            if (persistenceStarted && activeCall is not null && offlineDialogue is not null && callUseLease is not null)
            {
                _ = this.PersistCallHistoryAsync(activeCall, offlineDialogue, callUseLease);
            }
            if (activeCall is not null)
            {
                activeCall.UserAgent.ServerCallCancelled -= this.OnServerCallCancelled;
                activeCall.Dispose();
            }
        }

        public void CloseCallSession(ActiveCallContext activeCall)
        {
            IOfflineDialogue? offlineDialogue = null;
            IDisposable? callUseLease = null;
            bool persistenceStarted = this.TryBeginCallHistoryPersistence(activeCall, out offlineDialogue, out callUseLease);
            bool callDetached;
            lock (this._callSessionLock)
            {
                callDetached = ReferenceEquals(this._activeCall, activeCall);
                if (callDetached)
                {
                    this._activeCall = null;
                    this._pendingServerUserAgent = null;
                    this._callState = this._callbackActive
                        ? CallState.Ending
                        : CallState.Idle;
                }
            }

            if (!callDetached)
            {
                if (persistenceStarted)
                {
                    callUseLease?.Dispose();
                    this.EndCallHistoryPersistence();
                }
                return;
            }

            if (persistenceStarted && offlineDialogue is not null && callUseLease is not null)
            {
                _ = this.PersistCallHistoryAsync(activeCall, offlineDialogue, callUseLease);
            }
            activeCall.UserAgent.ServerCallCancelled -= this.OnServerCallCancelled;
            activeCall.Dispose();
        }

        private bool TryBeginCallHistoryPersistence(
            ActiveCallContext activeCall,
            out IOfflineDialogue? offlineDialogue,
            out IDisposable? callUseLease)
        {
            offlineDialogue = null;
            callUseLease = null;
            if (!activeCall.TryAcquireUse(out callUseLease) || callUseLease is null)
            {
                return false;
            }

            if (!activeCall.AIAgentContext.HasCompletedOnlineTurns ||
                activeCall.AIAgentContext.PrivateProvider.OfflineDialogue is not IOfflineDialogue provider)
            {
                callUseLease.Dispose();
                callUseLease = null;
                return false;
            }

            bool started;
            lock (this._callSessionLock)
            {
                started = ReferenceEquals(this._activeCall, activeCall) &&
                    !string.Equals(this._historyPersistenceCallId, activeCall.CallId, StringComparison.Ordinal);
                if (started)
                {
                    this._historyPersistenceCallId = activeCall.CallId;
                    this._persistenceCount++;
                    offlineDialogue = provider;
                }
            }

            if (!started)
            {
                callUseLease.Dispose();
                callUseLease = null;
            }

            return started;
        }

        private async Task PersistCallHistoryAsync(
            ActiveCallContext activeCall,
            IOfflineDialogue offlineDialogue,
            IDisposable callUseLease)
        {
            try
            {
                await offlineDialogue.PersistCompletedOnlineTurnsAsync(activeCall, CancellationToken.None);
            }
            catch (Exception exception)
            {
                this._logger.LogError(exception, "持久化设备 {deviceId} 的在线对话历史失败。", this.DeviceId);
            }
            finally
            {
                callUseLease.Dispose();
                this.EndCallHistoryPersistence();
            }
        }

        private void EndCallHistoryPersistence()
        {
            lock (this._callSessionLock)
            {
                if (this._persistenceCount > 0)
                {
                    this._persistenceCount--;
                }

                this._historyPersistenceCallId = null;
            }
        }

        private void OnServerCallCancelled(ISIPServerUserAgent serverUserAgent, SIPRequest request)
        {
            ActiveCallContext? activeCall;
            lock (this._callSessionLock)
            {
                activeCall = this._activeCall;
            }

            if (activeCall is not null)
            {
                this._logger.LogInformation(
                    "SIP ServerCallCancelled，关闭设备 {deviceId} 的 Call-ID {callId}。",
                    this.DeviceId,
                    request.Header.CallId);
                this.CloseCallSession(activeCall);
            }
        }

        private void ExpireRegistrationIfNeeded(DateTimeOffset now)
        {
            if (this._registrationState == RegistrationState.Registered &&
                this._registration?.IsExpired(now) == true)
            {
                this._registrationState = RegistrationState.Expired;
            }
        }

        public void Dispose()
        {
            this.CloseCallSession();
            this.EndCallback();
        }
    }
}
