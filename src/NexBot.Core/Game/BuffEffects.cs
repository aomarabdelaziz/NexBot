using System.Globalization;
using NexBot.Data;

namespace NexBot.Game;

/// <summary>
/// Reads a buff skill's effects from its params (the four-letter codes of the skill data, each followed by its
/// values) as the lines the game's buff card shows: "Physical damage 20% Increase", "Max HP 500 Increase" ...
/// Only the codes whose values are known are described; the rest are skipped.
/// </summary>
public static class BuffEffects
{
    private static int Code(string text) => text.Aggregate(0, (v, ch) => (v << 8) | ch);

    private static readonly int Dru = Code("dru"), Hpi = Code("hpi"), Mpi = Code("mpi"), Hste = Code("hste"), Hst2 = Code("hst2"),
        Hst3 = Code("hst3"), Stri = Code("stri"), Inti = Code("inti"), Odar = Code("odar"), Defp = Code("defp"), Defr = Code("defr"),
        Dgmp = Code("dgmp"), Gdr = Code("gdr"), Irgc = Code("irgc"), Spda = Code("spda"), Luck = Code("luck"), Alcu = Code("alcu");

    public static IReadOnlyList<string> Describe(SkillInfo? info)
    {
        var lines = new List<string>();
        if (info is null || string.IsNullOrEmpty(info.Params))
            return lines;
        var values = info.Params.Split('|').Select(v => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : 0).ToArray();
        long At(int i) => i < values.Length ? values[i] : 0;
        void Add(string what, long value, string unit = "")
        {
            if (value > 0)
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"{what} {value}{unit} Increase"));
            else if (value < 0)
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"{what} {-value}{unit} Decrease"));
        }

        for (int i = 0; i < values.Length; i++)
        {
            int code = (int)values[i];
            long a = At(i + 1), b = At(i + 2);
            if (code == Dru)
            {
                Add("Physical damage", a, "%");
                Add("Magical damage", b, "%");
            }
            else if (code == Hpi)
            {
                Add("Max HP", a);
                Add("Max HP", b, "%");
            }
            else if (code == Mpi)
            {
                Add("Max MP", a);
                Add("Max MP", b, "%");
            }
            else if (code == Hste || code == Hst2 || code == Hst3)
                Add("Moving speed", a, "%");
            else if (code == Stri)
                Add("STR", a);
            else if (code == Inti)
                Add("INT", a);
            else if (code == Odar && b > 0)
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"Damage absorption {b}%"));
            else if (code == Defp)
            {
                Add("Physical defense", a);
                Add("Magical defense", b);
            }
            else if (code == Defr)
            {
                Add("Physical defense", a, "%");
                Add("Magical defense", b, "%");
            }
            else if (code == Dgmp && a > 0)
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"{a}% of damage taken from MP"));
            else if (code == Gdr)
                Add("Gold drop", a, "%");
            else if (code == Irgc)
            {
                Add("HP recovery", a, "%");
                Add("MP recovery", b, "%");
            }
            else if (code == Spda)
            {
                Add("Shield physical defense", -a, "%");
                Add("Physical attack", b, "%");
            }
            else if (code == Luck)
                Add("Luck", a, "%");
            else if (code == Alcu)
                Add("Alchemy success rate", a, "%");
        }
        return lines.Distinct().ToList();
    }
}
