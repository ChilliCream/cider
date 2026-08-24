using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Cider.Core.Ids;

/// <summary>Container name validation and Docker-style random name generation.</summary>
public static partial class Names
{
    /// <summary>Docker's restricted name pattern (an optional leading '/' is tolerated).</summary>
    [GeneratedRegex(@"^/?[a-zA-Z0-9][a-zA-Z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DockerNameRegex();

    /// <summary>Apple container ids: <c>^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}$</c>.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex AppleContainerIdRegex();

    /// <summary><c>true</c> when Docker would accept <paramref name="name"/> as a container/network/volume name.</summary>
    public static bool IsValidDockerName(string? name) =>
        !string.IsNullOrEmpty(name) && DockerNameRegex().IsMatch(name);

    /// <summary><c>true</c> when the Apple <c>container</c> CLI would accept <paramref name="id"/> as an id.</summary>
    public static bool IsValidAppleContainerId(string? id) =>
        !string.IsNullOrEmpty(id) && AppleContainerIdRegex().IsMatch(id);

    /// <summary>Generates a Docker-style <c>adjective_surname</c> name.</summary>
    public static string GenerateRandomName()
    {
        while (true)
        {
            var adjective = Adjectives[RandomNumberGenerator.GetInt32(Adjectives.Length)];
            var surname = Surnames[RandomNumberGenerator.GetInt32(Surnames.Length)];
            var name = $"{adjective}_{surname}";

            // Docker refuses this one for historical reasons; keep the joke alive.
            if (name is "boring_wozniak")
            {
                continue;
            }

            return name;
        }
    }

    private static readonly string[] Adjectives =
    [
        "admiring", "affectionate", "amazing", "awesome", "blissful", "bold", "brave", "busy",
        "charming", "clever", "cool", "compassionate", "competent", "confident", "dazzling",
        "determined", "eager", "ecstatic", "elastic", "elated", "epic", "exciting", "fervent",
        "focused", "friendly", "gallant", "gifted", "goofy", "gracious", "great", "happy",
        "hopeful", "infallible", "jolly", "jovial", "keen", "kind", "laughing", "loving", "lucid",
        "magical", "modest", "musing", "naughty", "nifty", "nostalgic", "objective", "optimistic",
        "peaceful", "practical", "priceless", "quirky", "quizzical", "recursing", "relaxed",
        "reverent", "romantic", "serene", "sharp", "silly", "sleepy", "stoic", "strange",
        "stupefied", "suspicious", "sweet", "tender", "thirsty", "trusting", "upbeat", "vibrant",
        "vigilant", "vigorous", "wizardly", "wonderful", "xenodochial", "youthful", "zealous",
        "zen", "boring",
    ];

    private static readonly string[] Surnames =
    [
        "albattani", "allen", "almeida", "archimedes", "ardinghelli", "babbage", "banach",
        "bardeen", "bell", "benz", "bhabha", "blackwell", "bohr", "booth", "borg", "bose",
        "brahmagupta", "brown", "carson", "cartwright", "chandrasekhar", "chaum", "clarke",
        "colden", "cori", "curie", "darwin", "davinci", "diffie", "dijkstra", "dubinsky",
        "easley", "edison", "einstein", "elion", "engelbart", "euclid", "euler", "faraday",
        "fermat", "fermi", "feynman", "franklin", "galileo", "gates", "goldberg", "goldstine",
        "golick", "goodall", "hamilton", "hawking", "heisenberg", "hermann", "hodgkin", "hoover",
        "hopper", "hugle", "hypatia", "jang", "jennings", "jepsen", "johnson", "joliot", "jones",
        "kalam", "kare", "keller", "kepler", "khorana", "kilby", "kirch", "knuth", "kowalevski",
        "lalande", "lamarr", "lamport", "leakey", "leavitt", "lewin", "lichterman", "liskov",
        "lovelace", "lumiere", "mahavira", "margulis", "matsumoto", "maxwell", "mayer", "mccarthy",
        "mcclintock", "mclean", "mcnulty", "meitner", "mendel", "mendeleev", "meninsky", "merkle",
        "mestorf", "montalcini", "moore", "morse", "murdock", "napier", "nash", "neumann",
        "newton", "nightingale", "nobel", "noether", "northcutt", "noyce", "panini", "pare",
        "pasteur", "payne", "perlman", "pike", "poincare", "poitras", "ptolemy", "raman",
        "ramanujan", "ride", "ritchie", "roentgen", "rosalind", "rubin", "saha", "sammet",
        "shannon", "shaw", "shirley", "shockley", "sinoussi", "snyder", "spence", "stallman",
        "swanson", "swartz", "swirles", "tesla", "thompson", "torvalds", "turing", "varahamihira",
        "visvesvaraya", "volhard", "wescoff", "wiles", "williams", "wilson", "wozniak", "wright",
        "yalow", "yonath",
    ];
}
