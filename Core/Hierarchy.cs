using System.Globalization;

namespace ICD10Converter.Core;

/// <summary>Chapter / block / category lookups. Ported 1:1 from the PowerShell module so output is identical.</summary>
public static class Hierarchy
{
    static readonly Dictionary<string, string> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A00"] = "Cholera",
        ["A01"] = "Typhoid and paratyphoid fevers",
        ["A02"] = "Other salmonella infections",
        ["A03"] = "Shigellosis",
        ["A04"] = "Other bacterial intestinal infections",
        ["A05"] = "Other bacterial foodborne intoxications, not elsewhere classified",
        ["A06"] = "Amebiasis",
        ["A07"] = "Other protozoal intestinal diseases",
        ["A08"] = "Viral and other specified intestinal infections",
        ["A09"] = "Infectious gastroenteritis and colitis, unspecified",
    };

    static readonly Dictionary<string, string> Blocks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["A00-A09"] = "Intestinal infectious diseases (A00-A09)",
        ["A15-A19"] = "Tuberculosis (A15-A19)",
        ["A20-A28"] = "Certain zoonotic bacterial diseases (A20-A28)",
        ["A30-A49"] = "Other bacterial diseases (A30-A49)",
        ["A50-A64"] = "Infections with a predominantly sexual mode of transmission (A50-A64)",
        ["A65-A69"] = "Other spirochetal diseases (A65-A69)",
        ["A70-A74"] = "Other diseases caused by chlamydiae (A70-A74)",
        ["A75-A79"] = "Rickettsioses (A75-A79)",
        ["A80-A89"] = "Viral infections of the central nervous system (A80-A89)",
        ["A90-A99"] = "Arthropod-borne viral fevers and viral hemorrhagic fevers (A90-A99)",
        ["B00-B09"] = "Viral infections characterized by skin and mucous membrane lesions (B00-B09)",
        ["B10-B19"] = "Other human herpesviruses (B10-B19)",
        ["B20-B24"] = "Human immunodeficiency virus [HIV] disease (B20-B24)",
        ["B25-B34"] = "Other viral diseases (B25-B34)",
        ["B35-B49"] = "Mycoses (B35-B49)",
        ["B50-B64"] = "Protozoal diseases (B50-B64)",
        ["B65-B83"] = "Helminthiases (B65-B83)",
        ["B85-B89"] = "Pediculosis, acariasis and other infestations (B85-B89)",
        ["B90-B94"] = "Sequelae of infectious and parasitic diseases (B90-B94)",
        ["B95-B97"] = "Bacterial and viral infectious agents (B95-B97)",
        ["B99-B99"] = "Other infectious diseases (B99-B99)",
    };

    static readonly Dictionary<char, string> Chapters = new()
    {
        ['A'] = "Certain infectious and parasitic diseases (A00-B99)",
        ['B'] = "Certain infectious and parasitic diseases (A00-B99)",
        ['C'] = "Neoplasms (C00-D49)",
        ['D'] = "Diseases of the blood and blood-forming organs (D50-D89)",
        ['E'] = "Endocrine, nutritional and metabolic diseases (E00-E89)",
        ['F'] = "Mental, behavioral and neurodevelopmental disorders (F01-F99)",
        ['G'] = "Diseases of the nervous system (G00-G99)",
        ['H'] = "Diseases of the eye and adnexa (H00-H59)",
        ['I'] = "Diseases of the circulatory system (I00-I99)",
        ['J'] = "Diseases of the respiratory system (J00-J99)",
        ['K'] = "Diseases of the digestive system (K00-K95)",
        ['L'] = "Diseases of the skin and subcutaneous tissue (L00-L99)",
        ['M'] = "Diseases of the musculoskeletal system and connective tissue (M00-M99)",
        ['N'] = "Diseases of the genitourinary system (N00-N99)",
        ['O'] = "Pregnancy, childbirth and the puerperium (O00-O9A)",
        ['P'] = "Certain conditions originating in the perinatal period (P00-P96)",
        ['Q'] = "Congenital malformations, deformations and chromosomal abnormalities (Q00-Q99)",
        ['R'] = "Symptoms, signs and abnormal clinical and laboratory findings (R00-R99)",
        ['S'] = "Injury, poisoning and certain other consequences of external causes (S00-T88)",
        ['T'] = "Injury, poisoning and certain other consequences of external causes (S00-T88)",
        ['V'] = "External causes of morbidity (V00-Y99)",
        ['W'] = "External causes of morbidity (V00-Y99)",
        ['X'] = "External causes of morbidity (V00-Y99)",
        ['Y'] = "External causes of morbidity (V00-Y99)",
        ['Z'] = "Factors influencing health status and contact with health services (Z00-Z99)",
    };

    static readonly (int Lo, int Hi)[] RangesA =
        { (0, 9), (15, 19), (20, 28), (30, 49), (50, 64), (65, 69), (70, 74), (75, 79), (80, 89), (90, 99) };

    static readonly (int Lo, int Hi)[] RangesB =
        { (0, 9), (10, 19), (20, 24), (25, 34), (35, 49), (50, 64), (65, 83), (85, 89), (90, 94), (95, 97), (99, 99) };

    public static string CategoryDescription(string category) =>
        Categories.TryGetValue(category, out var d) ? d : category;

    public static string BlockDescription(string block) =>
        Blocks.TryGetValue(block, out var d) ? d : block;

    public static string ChapterDescription(string code) =>
        code.Length > 0 && Chapters.TryGetValue(char.ToUpperInvariant(code[0]), out var d) ? d : "Unknown chapter";

    public static string BlockRange(string code)
    {
        if (code.Length < 3) return code;

        char letter = code[0];
        string numStr = code.Substring(1, 2);

        if (!int.TryParse(numStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int num))
        {
            // Codes such as O9A / T4A: fall back to the first digit only.
            if (int.TryParse(code.AsSpan(1, 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out num))
            {
                int s = num / 10 * 10;
                return $"{letter}{s:00}-{letter}{s + 9:00}";
            }
            return code.Substring(0, 3);
        }

        switch (char.ToUpperInvariant(letter))
        {
            case 'A':
                foreach (var (lo, hi) in RangesA)
                    if (num >= lo && num <= hi) return $"{letter}{lo:00}-{letter}{hi:00}";
                break;
            case 'B':
                foreach (var (lo, hi) in RangesB)
                    if (num >= lo && num <= hi) return $"{letter}{lo:00}-{letter}{hi:00}";
                break;
            default:
                int start = num / 10 * 10;
                return $"{letter}{start:00}-{letter}{start + 9:00}";
        }
        return $"{letter}{numStr}";
    }
}
