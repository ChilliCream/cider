namespace Cider.Dns;

/// <summary>
/// Parsed/echoed contents of an EDNS0 OPT pseudo-record (RFC 6891). Only what this library needs:
/// the requestor's/responder's UDP payload size. Extended RCODE, version and flags (e.g. DO) are
/// carried through for completeness but this library never sets extended-rcode or DNSSEC bits itself.
/// </summary>
public sealed record EdnsOptions(int UdpPayloadSize, byte ExtendedRcode = 0, byte Version = 0, ushort Flags = 0)
{
    /// <summary>The UDP payload size Cider.Dns advertises when echoing EDNS0 on a response.</summary>
    public const int DefaultResponderPayloadSize = 1232;
}
