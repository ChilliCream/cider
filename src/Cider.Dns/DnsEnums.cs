namespace Cider.Dns;

/// <summary>DNS header OPCODE (RFC 1035 §4.1.1).</summary>
public enum DnsOpcode : byte
{
    Query = 0,
    IQuery = 1,
    Status = 2,
    Notify = 4,
    Update = 5,
}

/// <summary>DNS header RCODE (RFC 1035 §4.1.1). Only the base 4-bit values are needed here.</summary>
public enum DnsRcode : byte
{
    NoError = 0,
    FormErr = 1,
    ServFail = 2,
    NxDomain = 3,
    NotImp = 4,
    Refused = 5,
}

/// <summary>DNS resource record TYPE values (RFC 1035 / RFC 3596 / RFC 6891) that this library understands by name.
/// Any other numeric type value round-trips fine as an opaque record — cast to/from this enum freely.</summary>
public enum DnsRecordType : ushort
{
    A = 1,
    Ns = 2,
    Cname = 5,
    Soa = 6,
    Ptr = 12,
    Mx = 15,
    Txt = 16,
    Aaaa = 28,
    Opt = 41,
    Any = 255,
}

/// <summary>DNS CLASS values. For OPT pseudo-records this field is repurposed as the UDP payload size,
/// so arbitrary numeric values are expected and valid there.</summary>
public enum DnsClass : ushort
{
    In = 1,
    Any = 255,
}
