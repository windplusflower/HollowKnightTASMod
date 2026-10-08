namespace HollowKnightTAS.Core.Ipc
{
    public static class CompanionProtocolMetadata
    {
        public const string Product =
            "HollowKnightTAS.Companion";
        public const string Version = "0.1.8";
        public const int ProtocolMinimum = 1;
        public const int ProtocolMaximum = 1;

        public static ProtocolRange SupportedProtocols =>
            new ProtocolRange(
                ProtocolMinimum,
                ProtocolMaximum);
    }
}
