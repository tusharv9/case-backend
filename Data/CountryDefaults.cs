namespace CaseManagement.Api.Data;

/// <summary>
/// The initial rows of the Countries table, applied by the AddCountriesAndFieldMetadata migration (idempotently, so
/// an administrator's later edits are never overwritten). After that, countries are data: adding one or correcting a
/// number-length rule is a row edit, not a code change.
/// Columns: ISO2 | ISO3 | name | dial code | min national digits | max national digits.
/// </summary>
public static class CountryDefaults
{
    // Malaysia keeps the long-standing rule (10 digits after +60) so existing behaviour does not change.
    private const string Rows = @"
MY|MYS|Malaysia|60|10|10
SG|SGP|Singapore|65|8|8
IN|IND|India|91|10|10
ID|IDN|Indonesia|62|8|12
TH|THA|Thailand|66|8|9
VN|VNM|Vietnam|84|9|10
PH|PHL|Philippines|63|10|10
BN|BRN|Brunei|673|7|7
KH|KHM|Cambodia|855|8|9
LA|LAO|Laos|856|8|10
MM|MMR|Myanmar|95|8|10
CN|CHN|China|86|11|11
HK|HKG|Hong Kong|852|8|8
MO|MAC|Macao|853|8|8
TW|TWN|Taiwan|886|9|10
JP|JPN|Japan|81|10|10
KR|KOR|South Korea|82|9|10
MN|MNG|Mongolia|976|8|8
LK|LKA|Sri Lanka|94|9|9
BD|BGD|Bangladesh|880|10|10
PK|PAK|Pakistan|92|10|10
NP|NPL|Nepal|977|10|10
MV|MDV|Maldives|960|7|7
AF|AFG|Afghanistan|93|9|9
AU|AUS|Australia|61|9|9
NZ|NZL|New Zealand|64|8|10
US|USA|United States|1|10|10
CA|CAN|Canada|1|10|10
MX|MEX|Mexico|52|10|10
BR|BRA|Brazil|55|10|11
AR|ARG|Argentina|54|10|11
CL|CHL|Chile|56|9|9
CO|COL|Colombia|57|10|10
PE|PER|Peru|51|9|9
GB|GBR|United Kingdom|44|9|10
IE|IRL|Ireland|353|8|9
FR|FRA|France|33|9|9
DE|DEU|Germany|49|10|11
IT|ITA|Italy|39|9|11
ES|ESP|Spain|34|9|9
PT|PRT|Portugal|351|9|9
NL|NLD|Netherlands|31|9|9
BE|BEL|Belgium|32|8|9
CH|CHE|Switzerland|41|9|9
AT|AUT|Austria|43|10|11
SE|SWE|Sweden|46|7|10
NO|NOR|Norway|47|8|8
DK|DNK|Denmark|45|8|8
FI|FIN|Finland|358|9|10
PL|POL|Poland|48|9|9
CZ|CZE|Czechia|420|9|9
SK|SVK|Slovakia|421|9|9
HU|HUN|Hungary|36|8|9
RO|ROU|Romania|40|9|9
BG|BGR|Bulgaria|359|8|9
GR|GRC|Greece|30|10|10
HR|HRV|Croatia|385|8|9
RS|SRB|Serbia|381|8|9
UA|UKR|Ukraine|380|9|9
LT|LTU|Lithuania|370|8|8
LV|LVA|Latvia|371|8|8
EE|EST|Estonia|372|7|8
RU|RUS|Russia|7|10|10
KZ|KAZ|Kazakhstan|7|10|10
UZ|UZB|Uzbekistan|998|9|9
AZ|AZE|Azerbaijan|994|9|9
GE|GEO|Georgia|995|9|9
AM|ARM|Armenia|374|8|8
TR|TUR|Turkey|90|10|10
IL|ISR|Israel|972|8|9
AE|ARE|United Arab Emirates|971|8|9
SA|SAU|Saudi Arabia|966|9|9
QA|QAT|Qatar|974|8|8
KW|KWT|Kuwait|965|8|8
BH|BHR|Bahrain|973|8|8
OM|OMN|Oman|968|8|8
JO|JOR|Jordan|962|8|9
LB|LBN|Lebanon|961|7|8
IQ|IRQ|Iraq|964|10|10
IR|IRN|Iran|98|10|10
EG|EGY|Egypt|20|10|10
MA|MAR|Morocco|212|9|9
DZ|DZA|Algeria|213|8|9
TN|TUN|Tunisia|216|8|8
NG|NGA|Nigeria|234|10|10
GH|GHA|Ghana|233|9|9
KE|KEN|Kenya|254|9|9
TZ|TZA|Tanzania|255|9|9
UG|UGA|Uganda|256|9|9
ET|ETH|Ethiopia|251|9|9
ZA|ZAF|South Africa|27|9|9
";

    public static IEnumerable<(string Iso2, string Iso3, string Name, string Dial, int Min, int Max)> All()
    {
        foreach (var line in Rows.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split('|');
            yield return (p[0], p[1], p[2], p[3], int.Parse(p[4]), int.Parse(p[5]));
        }
    }

    /// <summary>Idempotent INSERT of every default country that is not already present (matched by ISO2).</summary>
    public static string InsertSql()
    {
        var values = string.Join(",\n", All().Select((c, i) =>
            $"('{c.Iso2}', '{c.Iso3}', '{c.Name.Replace("'", "''")}', '{c.Dial}', {c.Min}, {c.Max}, {i + 1})"));
        return $@"
INSERT INTO ""Countries"" (""Id"", ""Iso2"", ""Iso3"", ""Name"", ""DialCode"", ""MinNationalDigits"", ""MaxNationalDigits"", ""IsActive"", ""CreatedAt"")
SELECT gen_random_uuid(), v.iso2, v.iso3, v.name, v.dial, v.mn, v.mx, TRUE, NOW()
  FROM (VALUES
{values}
       ) AS v(iso2, iso3, name, dial, mn, mx, ord)
 WHERE NOT EXISTS (SELECT 1 FROM ""Countries"" c WHERE c.""Iso2"" = v.iso2);
";
    }
}
