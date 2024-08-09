using System.Diagnostics;
using System.Text.RegularExpressions;
using ketchupbot_updater.API;
using Serilog;

namespace ketchupbot_updater;

/// <summary>
/// Ship updater class to facilitate updating ship pages. You should pass this class to other classes via dependency injection.
/// </summary>
/// <param name="bot">The <see cref="MwClient"/> instance to use for interacting with the wiki</param>
/// <param name="apiManager">The <see cref="ApiManager"/> instance to use for making API requests</param>
public partial class ShipUpdater(MwClient bot, ApiManager apiManager)
{
    private static string GetShipName(string data) => GlobalConfiguration.ShipNameMap.GetValueOrDefault(data, data);

    private const int MaxLength = 12;

    /// <summary>
    /// Mass update all ships with the provided data. If no data is provided, it will fetch the data from the API.
    /// </summary>
    /// <param name="shipDatas">The ship data to use during the update run</param>
    /// <param name="threads"></param>
    public async Task UpdateAllShips(Dictionary<string, Dictionary<string, string>>? shipDatas = null,
        int threads = -1)
    {
        Dictionary<string, Dictionary<string, string>>? allShips = await apiManager.GetShipsData();
        ArgumentNullException.ThrowIfNull(allShips);

        await MassUpdateShips(allShips.Keys.ToList(), shipDatas, threads);
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="ships"></param>
    /// <param name="shipDatas"></param>
    /// <param name="threads"></param>
    public async Task MassUpdateShips(List<string> ships, Dictionary<string, Dictionary<string, string>>? shipDatas = null, int threads = -1)
    {
        var massUpdateStart = Stopwatch.StartNew();
        shipDatas ??= await apiManager.GetShipsData();
        ArgumentNullException.ThrowIfNull(shipDatas);

        await Parallel.ForEachAsync(ships, new ParallelOptions {
            MaxDegreeOfParallelism = threads
        }, async (ship, _) =>
        {
            try
            {
#if DEBUG
                var updateStart = Stopwatch.StartNew();
#endif
                Log.Information($"{GetShipIdentifier(ship)} Updating ship...");
                await UpdateShip(ship, shipDatas.GetValueOrDefault(ship));
#if DEBUG
                updateStart.Stop();
                Log.Information($"{GetShipIdentifier(ship)} Updated ship in {updateStart.ElapsedMilliseconds}ms");
#else
                Log.Information($"{GetShipIdentifier(ship)} Updated ship");
#endif
            }
            catch (Exception e)
            {
                Log.Error(e, $"{GetShipIdentifier(ship)} Failed to update ship");
            }
        });

        massUpdateStart.Stop();
        Log.Information($"Finished updating all ships in {massUpdateStart.ElapsedMilliseconds/1000}s");
    }

    /// <summary>
    /// Update a singular ship page with the provided data (or fetch it if not provided)
    /// </summary>
    /// <param name="ship">The name of the ship to update</param>
    /// <param name="data">Supply a <see cref="Dictionary{TKey,TValue}"/> to use for updating. If left null, it will be fetched for you, but this is very bandwidth intensive for mass updating. It is better to grab it beforehand, filter the data for the specific <see cref="Dictionary{TKey,TValue}"/> needed, and pass that to the functions.</param>
    private async Task UpdateShip(string ship, Dictionary<string, string>? data = null)
    {
        ship = GetShipName(ship);

        #region Data Fetching Logic
        if (data == null)
        {
            Dictionary<string, Dictionary<string, string>>? shipStats = await apiManager.GetShipsData();

            Dictionary<string, string>? shipData = (shipStats ?? throw new InvalidOperationException("Failed to get ship data")).GetValueOrDefault(ship ?? throw new InvalidOperationException("No ship name provided"));

            if (shipData == null)
            {
                Log.Error("Ship not found in API data: " + ship);
                return;
            }

            data = shipData;
        }
        #endregion

        #region Article Fetch Logic
#if DEBUG
        var fetchArticleStart = Stopwatch.StartNew();
#endif

        string article = await bot.GetArticle(ship); // Throws exception if article does not exist

#if DEBUG
        fetchArticleStart.Stop();
        Log.Debug($"{GetShipIdentifier(ship)} Fetched article in {fetchArticleStart.ElapsedMilliseconds}ms");
#endif
        #endregion

        if (IGNORE_FLAG_REGEX().IsMatch(article.ToLower())) throw new InvalidOperationException("Found ignore flag in article");

        #region Infobox Parsing Logic
#if DEBUG
        var parsingInfoboxStart = Stopwatch.StartNew();
#endif

        Dictionary<string, string> parsedInfobox = WikiParser.ParseInfobox(WikiParser.ExtractInfobox(article));

#if DEBUG
        parsingInfoboxStart.Stop();
        Log.Debug($"{GetShipIdentifier(ship)} Parsed infobox in {parsingInfoboxStart.ElapsedMilliseconds}ms");
#endif
        #endregion

        #region Data merging logic
#if DEBUG
        var mergeDataStart = Stopwatch.StartNew();
#endif

        Tuple<Dictionary<string, string>, List<string>> mergedData = WikiParser.MergeData(data ?? throw new InvalidOperationException("Supplied data is null after deserialization"), parsedInfobox);

#if DEBUG
        mergeDataStart.Stop();
        Log.Debug($"{GetShipIdentifier(ship)} Merged data in {mergeDataStart.ElapsedMilliseconds}ms");
#endif
        #endregion

        #region Data Sanitization Logic
#if DEBUG
        var sanitizeDataStart = Stopwatch.StartNew();
#endif

        Tuple<Dictionary<string, string>, List<string>> sanitizedData = WikiParser.SanitizeData(mergedData.Item1, parsedInfobox);

#if DEBUG
        sanitizeDataStart.Stop();
        Log.Debug($"{GetShipIdentifier(ship)} Sanitized data in {sanitizeDataStart.ElapsedMilliseconds}ms");
#endif
        #endregion

        #region Diffing logic
        if (!WikiParser.CheckIfInfoboxesChanged(sanitizedData.Item1, parsedInfobox))
            throw new InvalidOperationException("No changes detected");

        // The below logic is only for debugging/development instances to see what changes are being made to the infobox. It is not necessary for the bot to function, so it should not be in production.
        // I've turned it off cuz its kinda annoying.
        // Might add a CLI argument to enable it later.
#if DEBUG
        // var jdp = new JsonDiffPatch();
        // string? diff = jdp.Diff(JsonConvert.SerializeObject(sanitizedData.Item1, Formatting.Indented), JsonConvert.SerializeObject(parsedInfobox, Formatting.Indented));

        // if (!string.IsNullOrEmpty(diff))
        //     Console.WriteLine($"Diff:\n{diff}");
#endif
        #endregion

        #region Wikitext Construction Logic
#if DEBUG
        var wikitextConstructionStart = Stopwatch.StartNew();
#endif

        string newWikitext = WikiParser.ReplaceInfobox(article, WikiParser.ObjectToWikitext(sanitizedData.Item1));

#if DEBUG
        wikitextConstructionStart.Stop();
        Log.Debug($"{GetShipIdentifier(ship)} Constructed wikitext in {wikitextConstructionStart.ElapsedMilliseconds}ms");
#endif
        #endregion

        #region Article Editing Logic
#if DEBUG
        var articleEditStart = Stopwatch.StartNew();
#endif

        // TODO: Make the edit summary more descriptive. Add in added, changed, and removed parameters.
        await bot.EditArticle(ship, newWikitext, "Automated ship data update");

#if DEBUG
        articleEditStart.Stop();
        Log.Debug($"{GetShipIdentifier(ship)} Edited page in {articleEditStart.ElapsedMilliseconds}ms");
#endif
        #endregion
    }

    /// <summary>
    /// Used to get a ship identifier for logging purposes. I'm pretty sure this only runs in debug mode, so it should be okay to have the overhead from the string formatting.
    /// </summary>
    /// <param name="ship"></param>
    /// <returns></returns>
    private static string GetShipIdentifier(string ship)
    {
        string truncatedShipName = ship.Length > MaxLength ? ship[..MaxLength] : ship;
        string paddedShipName = truncatedShipName.PadRight(MaxLength);

        return $"{paddedShipName} {Environment.CurrentManagedThreadId,-2} |";
    }

    [GeneratedRegex(@"<!--\s*ketchupbot-ignore\s*-->", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex IGNORE_FLAG_REGEX();
}