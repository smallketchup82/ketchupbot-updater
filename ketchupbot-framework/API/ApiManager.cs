using ketchupbot_framework.Types;
using Microsoft.Extensions.Caching.Memory;
using Newtonsoft.Json;

namespace ketchupbot_framework.API;

/// <summary>
///     API Manager for interacting with the Galaxy Info API. Responsible for fetching, processing, and returning properly
///     formatted data from the Galaxy Info API.
/// </summary>
/// <param name="galaxyInfoApi">The base URL of the Galaxy Info API</param>
/// <param name="httpClient">The <see cref="HttpClient" /> instance to use for making API requests</param>
/// <param name="cache">
///     The <see cref="IMemoryCache" /> instance to use for caching data. May be left null to disable
///     caching
/// </param>
public class ApiManager(string galaxyInfoApi, HttpClient httpClient, IMemoryCache? cache)
{
    /// <summary>
    ///     Get ship data from the Galaxy Info API
    /// </summary>
    /// <returns>A list of ships from the Galaxy Info API with their respective data attributes</returns>
    public async Task<Dictionary<string, Dictionary<string, string>>> GetShipsData()
    {
        using HttpResponseMessage response = await httpClient.GetAsync($"{galaxyInfoApi.Trim()}/api/v2/galaxypedia");

        try
        {
            response.EnsureSuccessStatusCode();

            // If the data is already cached, return the cached data
            if (cache != null &&
                cache.TryGetValue("ShipData", out object? value) &&
                value is Dictionary<string, Dictionary<string, string>> cachedData) return cachedData;

            string jsonResponse = await response.Content.ReadAsStringAsync();

            // Deserialize the response into a Dictionary<string, Dictionary<string, string>> because the json is formatted as:
            // {
            //   "shiptitle":
            //   {
            //     "attribute": "value",
            //   }
            // }

            Dictionary<string, Dictionary<string, string>> deserializedResponse =
                JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(jsonResponse) ??
                throw new InvalidOperationException("Failed to deserialize ship data from the Galaxy Info API");

            cache?.Set("ShipData", deserializedResponse, new MemoryCacheEntryOptions
            {
                // Cache the data for 30 minutes before refreshing
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
            });

            return deserializedResponse;
        }
        catch (Exception e)
        {
            // If the data is already cached, return the cached data (since the API request failed)
            if (cache != null &&
                cache.TryGetValue("ShipData", out object? value) &&
                value is Dictionary<string, Dictionary<string, string>> cachedData) return cachedData;

            throw new InvalidOperationException("Failed to fetch ship data from the Galaxy Info API", e);
        }
    }

    /// <summary>
    ///     Get turret data from the upstream API
    /// </summary>
    /// <returns>A dictionary with the turret data</returns>
    public async Task<Dictionary<string, TurretData>?> GetTurretData()
    {
        HttpResponseMessage response = await httpClient.GetAsync($"{galaxyInfoApi.Trim()}/api/v2/ships-turret/raw");

        response.EnsureSuccessStatusCode();

        string stringResponse = await response.Content.ReadAsStringAsync();

        Console.WriteLine(stringResponse);

        dynamic? jsonResponse = JsonConvert.DeserializeObject<dynamic>(stringResponse);

        return JsonConvert.DeserializeObject<Dictionary<string, TurretData>>(jsonResponse?.serializedTurrets);
    }
}