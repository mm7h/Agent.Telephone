using Agent.Telephone.Abstractions.Configs;
using Agent.Telephone.Common.Enums;
using Agent.Telephone.Helpers;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace Agent.Telephone.Common.Contexts
{
    internal sealed class DeviceContext : IDisposable
    {
        private readonly SIPTransport _sipTransport;
        private readonly object _callSessionLock = new();
        private readonly object _registrationLock = new();
        private ActiveCallContext? _activeCall;
        private SIPServerUserAgent? _pendingServerUserAgent;
        private RegistrationBinding? _registration;
        private RegistrationState _registrationState;
        private CallState _callState;
        private bool _callbackActive;

        public DeviceContext(
            SIPTransport sipTransport,
            SIPRequest sipRequest,
            SIPURI contact,
            int expiresSeconds,
            List<AssistantConfig> availableAssistants)
        {
            this._sipTransport = sipTransport;

            this.DeviceId = sipRequest.GetDeviceId();
            this.AvailableAssistants = availableAssistants.ToDictionary(i => i.DialingNumber).AsReadOnly();
            this.AudioInPacket = new AudioInPacket();
            this.AudioOutputPacket = new AudioOutputPacket();
            this.LoginTime = DateTimeOffset.Now;
            this.LastActivityTime = this.LoginTime;
            this.UpdateRegistration(sipRequest, contact, expiresSeconds);
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
        public AudioOutputPacket AudioOutputPacket { get; }
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
                    return this._activeCall is not null || this._callbackActive;
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
                if (this._activeCall is not null || this._callbackActive)
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

        public void MarkTransferDialing(ActiveCallContext activeCall)
        {
            lock (this._callSessionLock)
            {
                if (ReferenceEquals(this._activeCall, activeCall))
                {
                    this._callState = CallState.TransferDialing;
                }
            }
        }

        public void MarkBridged(ActiveCallContext activeCall)
        {
            lock (this._callSessionLock)
            {
                if (ReferenceEquals(this._activeCall, activeCall))
                {
                    this._callState = CallState.Bridged;
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
            lock (this._callSessionLock)
            {
                activeCall = this._activeCall;
                this._activeCall = null;
                this._pendingServerUserAgent = null;
                if (activeCall is not null)
                {
                    this._callState = this._callbackActive
                        ? CallState.Ending
                        : CallState.Idle;
                }
                else if (!this._callbackActive)
                {
                    this._callState = CallState.Idle;
                }
            }

            if (activeCall is not null)
            {
                activeCall.UserAgent.ServerCallCancelled -= this.OnServerCallCancelled;
                activeCall.Dispose();
            }
        }

        public void CloseCallSession(ActiveCallContext activeCall)
        {
            lock (this._callSessionLock)
            {
                if (!ReferenceEquals(this._activeCall, activeCall))
                {
                    return;
                }

                this._activeCall = null;
                this._pendingServerUserAgent = null;
                this._callState = this._callbackActive
                    ? CallState.Ending
                    : CallState.Idle;
            }

            activeCall.UserAgent.ServerCallCancelled -= this.OnServerCallCancelled;
            activeCall.Dispose();
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
