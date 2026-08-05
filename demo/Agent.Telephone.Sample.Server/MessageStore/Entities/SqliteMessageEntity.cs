using System.Globalization;
using Agent.Telephone.Abstractions.Persistence;

namespace Agent.Telephone.Sample.Server.MessageStore.Entities
{
    internal sealed record SqliteMessageEntity(
        string Id,
        string TurnId,
        string UserAor,
        string AssistantNumber,
        string AudioPath,
        long Sequence,
        DeliveryState State,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? ReadAt,
        bool IsDelete)
    {
        public static SqliteMessageEntity FromReader(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            (DeliveryState)reader.GetInt32(6),
            DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(9)
                ? null
                : DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.GetInt32(10) != 0);

        public MessageRecord ToRecord() => new()
        {
            Id = this.Id,
            TurnId = this.TurnId,
            UserAor = this.UserAor,
            AssistantNumber = this.AssistantNumber,
            AudioPath = this.AudioPath,
            Sequence = this.Sequence,
            State = this.State,
            CreatedAt = this.CreatedAt,
            UpdatedAt = this.UpdatedAt,
            ReadAt = this.ReadAt,
        };
    }
}
