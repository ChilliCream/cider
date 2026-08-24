namespace Cider.Dns;

/// <summary>A single DNS question (RFC 1035 §4.1.2).</summary>
public sealed record DnsQuestion(string Name, DnsRecordType Type, DnsClass Class = DnsClass.In);
