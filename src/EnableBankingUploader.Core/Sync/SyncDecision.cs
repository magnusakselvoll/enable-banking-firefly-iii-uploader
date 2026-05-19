namespace EnableBankingUploader.Core.Sync;

public enum SyncDecision
{
    Create,
    SkipDuplicate,
    SkipNonBooked,
    SkipNoId,
}
