using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Linq;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;

[ApiController]
[Route("[controller]")]
public class AASSubmodelMatchController : ControllerBase
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AASSubmodelMatchController> _logger;

    public AASSubmodelMatchController(HttpClient httpClient, ILogger<AASSubmodelMatchController> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public class MatchSubmodelsRequest
    {
        public string ArticleAssetId { get; set; }
        public List<string> TestAdapterAssetIds { get; set; } = new List<string>();
        public List<string> TestDeviceAssetIds { get; set; } = new List<string>();
    }

    /// <summary>
    /// Matches submodels based on the provided request
    /// </summary>
    /// <param name="request">The request containing asset IDs for matching</param>
    /// <returns>A result containing matched submodels</returns>
    /// <response code="200">Returns the match result</response>
    /// <response code="400">If the request is invalid</response>
    /// <response code="500">If an error occurs during processing</response>
    /// /// <remarks>
    /// Sample requests:
    ///
    /// 1. Full request with article, adapters, and devices:
    /// 
    ///     POST /AASSubmodelMatch/match
    ///     {
    ///         "articleAssetId": "{\"name\": \"MVK_Pro_MPNIO_DIO8IOL8M12L_01\",\"value\": \"AssetMVK_Pro_MPNIO_DIO8IOL8M12L_Value\"}",
    ///         "testAdapterAssetIds": [
    ///             "{\"name\": \"PA210\",\"value\": \"AssetPA210_Value\"}",
    ///             "{\"name\": \"PA220\",\"value\": \"AssetPA220_Value\"}"
    ///         ],
    ///         "testDeviceAssetIds": [
    ///             "{\"name\": \"PG210\",\"value\": \"AssetPG210_Value\"}",
    ///             "{\"name\": \"PG220\",\"value\": \"AssetPG220_Value\"}"
    ///         ]
    ///     }
    ///
    /// 2. Partial request with article and adapters:
    ///
    ///     POST /AASSubmodelMatch/match
    ///     {
    ///         "articleAssetId": "{\"name\": \"MVK_Pro_MPNIO_DIO8IOL8M12L_01\",\"value\": \"AssetMVK_Pro_MPNIO_DIO8IOL8M12L_Value\"}",
    ///         "testAdapterAssetIds": [
    ///             "{\"name\": \"PA210\",\"value\": \"AssetPA210_Value\"}",
    ///             "{\"name\": \"PA220\",\"value\": \"AssetPA220_Value\"}"
    ///         ]
    ///     }
    ///
    /// 3. Request with only adapters and devices:
    ///
    ///     POST /AASSubmodelMatch/match
    ///     {
    ///         "testAdapterAssetIds": [
    ///             "{\"name\": \"PA210\",\"value\": \"AssetPA210_Value\"}",
    ///             "{\"name\": \"PA220\",\"value\": \"AssetPA220_Value\"}"
    ///         ],
    ///         "testDeviceAssetIds": [
    ///             "{\"name\": \"PG210\",\"value\": \"AssetPG210_Value\"}",
    ///             "{\"name\": \"PG220\",\"value\": \"AssetPG220_Value\"}"
    ///         ]
    ///     }
    /// </remarks>
    [HttpPost("match")]
    public async Task<IActionResult> MatchSubmodels([FromBody] MatchSubmodelsRequest request)
    {
        _logger.LogInformation("Received request: {Request}", JsonSerializer.Serialize(request));

        try
        {
            if (string.IsNullOrEmpty(request.ArticleAssetId))
            {
                if (request.TestAdapterAssetIds.Count == 0 || request.TestDeviceAssetIds.Count == 0)
                {
                    return BadRequest("Either ArticleAssetId must be provided, or both TestAdapterAssetIds and TestDeviceAssetIds must be non-empty.");
                }

                // Handle request without ArticleAssetId
                return await HandleAdapterDeviceOnlyRequest(request);
            }
            else
            {
                // Handle requests with ArticleAssetId
                return await HandleArticleBasedRequest(request);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while processing the request");
            return StatusCode(500, $"An error occurred: {ex.Message}");
        }
    }

    private async Task<IActionResult> HandleAdapterDeviceOnlyRequest(MatchSubmodelsRequest request)
    {
        var encodedTestAdapterAssetIds = request.TestAdapterAssetIds.ConvertAll(Base64UrlEncode);
        var encodedTestDeviceAssetIds = request.TestDeviceAssetIds.ConvertAll(Base64UrlEncode);

        var matchResult = new MatchResult
        {
            AdapterDevicePairings = new List<AdapterDevicePairing>()
        };

        foreach (var (adapterAssetId, encodedAdapterAssetId) in request.TestAdapterAssetIds.Zip(encodedTestAdapterAssetIds, (id, encoded) => (id, encoded)))
        {
            var adapterAas = await GetAasFromAssetId(encodedAdapterAssetId);
            _logger.LogInformation("Adapter AAS: {AdapterAas}", GetSnippet(adapterAas.ToString()));

            var adapterProperties = GetTechnicalProperties(adapterAas, isArticle: false);

            var matchingDevices = await GetMatchingDevices(request.TestDeviceAssetIds, encodedTestDeviceAssetIds, adapterAas);
            
            if (matchingDevices.Any())
            {
                matchResult.AdapterDevicePairings.Add(new AdapterDevicePairing
                {
                    TestAdapter = new TestAdapter
                    {
                        TestAdapterAssetId = AssetId.FromJson(adapterAssetId),
                        TestAdapterType = adapterProperties.TryGetValue("AdapterType", out var adapterType) ? adapterType : null,
                        InsertSurface = adapterProperties.TryGetValue("InsertSurface", out var insertSurface) ? insertSurface : null
                    },
                    TestDeviceAssetIds = matchingDevices.Select(AssetId.FromJson).ToList()
                });
            }
        }

        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
        };

        _logger.LogInformation("Final Matches: {Matches}", JsonSerializer.Serialize(matchResult, options));
        return Ok(JsonSerializer.Serialize(matchResult, options));
    }

    private async Task<IActionResult> HandleArticleBasedRequest(MatchSubmodelsRequest request)
    {
        string encodedArticleAssetId = Base64UrlEncode(request.ArticleAssetId);
        var articleAas = await GetAasFromAssetId(encodedArticleAssetId);
        _logger.LogInformation("Article AAS: {ArticleAas}", GetSnippet(articleAas.ToString()));

        var encodedTestAdapterAssetIds = request.TestAdapterAssetIds.ConvertAll(Base64UrlEncode);

        if (request.TestDeviceAssetIds.Count == 0)
        {
            // Handle partial request (only adapters)
            var partialMatchResult = new PartialMatchResult
            {
                Asset = new AssetIdWrapper { ArticleAssetId = AssetId.FromJson(request.ArticleAssetId) },
                Adapters = new List<TestAdapter>()
            };

            foreach (var (adapterAssetId, encodedAdapterAssetId) in request.TestAdapterAssetIds.Zip(encodedTestAdapterAssetIds, (id, encoded) => (id, encoded)))
            {
                var adapterAas = await GetAasFromAssetId(encodedAdapterAssetId);
                _logger.LogInformation("Adapter AAS: {AdapterAas}", GetSnippet(adapterAas.ToString()));

                var adapterProperties = GetTechnicalProperties(adapterAas, isArticle: false);

                if (IsAdapterCompatibleWithArticle(articleAas, adapterAas))
                {
                    partialMatchResult.Adapters.Add(new TestAdapter
                    {
                        TestAdapterAssetId = AssetId.FromJson(adapterAssetId),
                        TestAdapterType = adapterProperties.TryGetValue("AdapterType", out var adapterType) ? adapterType : null,
                        InsertSurface = adapterProperties.TryGetValue("InsertSurface", out var insertSurface) ? insertSurface : null
                    });
                }
            }

            var options = new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = true
            };

            _logger.LogInformation("Partial Match Result: {Matches}", JsonSerializer.Serialize(partialMatchResult, options));
            return Ok(JsonSerializer.Serialize(partialMatchResult, options));
        }
        else
        {
            // Handle full request (article, adapters, and devices)
            var matchResult = new MatchResult
            {
                Asset = new AssetIdWrapper { ArticleAssetId = AssetId.FromJson(request.ArticleAssetId) },
                AdapterDevicePairings = new List<AdapterDevicePairing>()
            };

            var encodedTestDeviceAssetIds = request.TestDeviceAssetIds.ConvertAll(Base64UrlEncode);

            foreach (var (adapterAssetId, encodedAdapterAssetId) in request.TestAdapterAssetIds.Zip(encodedTestAdapterAssetIds, (id, encoded) => (id, encoded)))
            {
                var adapterAas = await GetAasFromAssetId(encodedAdapterAssetId);
                _logger.LogInformation("Adapter AAS: {AdapterAas}", GetSnippet(adapterAas.ToString()));

                var adapterProperties = GetTechnicalProperties(adapterAas, isArticle: false);

                if (IsAdapterCompatibleWithArticle(articleAas, adapterAas))
                {
                    var matchingDevices = await GetMatchingDevices(request.TestDeviceAssetIds, encodedTestDeviceAssetIds, adapterAas);

                    if (matchingDevices.Any())
                    {
                        matchResult.AdapterDevicePairings.Add(new AdapterDevicePairing
                        {
                            TestAdapter = new TestAdapter
                            {
                                TestAdapterAssetId = AssetId.FromJson(adapterAssetId),
                                TestAdapterType = adapterProperties.TryGetValue("AdapterType", out var adapterType) ? adapterType : null,
                                InsertSurface = adapterProperties.TryGetValue("InsertSurface", out var insertSurface) ? insertSurface : null
                            },
                            TestDeviceAssetIds = matchingDevices.Select(AssetId.FromJson).ToList()
                        });
                    }
                }
            }

            var options = new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = true
            };

            _logger.LogInformation("Final Matches: {Matches}", JsonSerializer.Serialize(matchResult, options));
            return Ok(JsonSerializer.Serialize(matchResult, options));
        }
    }

    private async Task<List<string>> GetMatchingAdapters(List<string> adapterAssetIds, List<string> encodedAdapterAssetIds, JsonObject articleAas)
    {
        var matchingAdapters = new List<string>();

        for (int i = 0; i < encodedAdapterAssetIds.Count; i++)
        {
            var adapterAas = await GetAasFromAssetId(encodedAdapterAssetIds[i]);
            _logger.LogInformation("Adapter AAS: {AdapterAas}", GetSnippet(adapterAas.ToString()));

            if (IsAdapterCompatibleWithArticle(articleAas, adapterAas))
            {
                matchingAdapters.Add(adapterAssetIds[i]);
            }
        }

        return matchingAdapters;
    }

    private async Task<JsonObject> GetAasFromAssetId(string encodedAssetId)
    {
        _logger.LogInformation("Getting AAS for Asset ID: {AssetId}", encodedAssetId);
        var response = await _httpClient.GetAsync($"http://aas-lookup-service:80/AASLookup/lookup?assetId={encodedAssetId}&submodels=true");
        
        _logger.LogInformation("HTTP Response: {StatusCode}, {Headers}", response.StatusCode, response.Headers);
        
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        _logger.LogInformation("Raw AAS content received: {Content}", GetSnippet(content));
        
        var jsonArray = JsonNode.Parse(content) as JsonArray;
        
        if (jsonArray == null || jsonArray.Count == 0)
        {
            _logger.LogWarning("No AAS data found for Asset ID: {AssetId}", encodedAssetId);
            return new JsonObject();
        }
        
        var firstItem = jsonArray[0] as JsonObject;
        
        if (firstItem != null)
        {
            return firstItem;
        }
        
        _logger.LogWarning("Unexpected AAS data format for Asset ID: {AssetId}. Content: {Content}", encodedAssetId, firstItem?.ToString());
        return new JsonObject();
    }

    private Dictionary<string, string> GetTechnicalProperties(JsonObject aas, bool isArticle)
    {
        var result = new Dictionary<string, string>();

        var submodels = aas["submodels"] as JsonArray;
        var technicalDataSubmodel = submodels?.FirstOrDefault(sm => sm?["idShort"]?.ToString() == "TechnicalData") as JsonObject;

        if (technicalDataSubmodel != null)
        {
            var submodelElements = technicalDataSubmodel["submodelElements"] as JsonArray;
            var technicalProperties = submodelElements?.FirstOrDefault(sme => sme?["idShort"]?.ToString() == "TechnicalProperties") as JsonObject;

            if (technicalProperties != null)
            {
                var properties = technicalProperties["value"] as JsonArray;
                foreach (var prop in properties ?? Enumerable.Empty<JsonNode>())
                {
                    var idShort = prop?["idShort"]?.ToString();
                    var value = prop?["value"];

                    if (isArticle && idShort == "ListOfHousingNumbers")
                    {
                        if (value is JsonArray valueArray)
                        {
                            var housingNumbers = valueArray
                                .Select(item => item?["value"]?.ToString())
                                .Where(v => !string.IsNullOrEmpty(v))
                                .ToList();
                            result["ListOfHousingNumbers"] = JsonSerializer.Serialize(housingNumbers);
                        }
                    }
                    else if (!isArticle && (idShort == "InsertSurface" || idShort == "AdapterType"))
                    {
                        result[idShort] = value?.ToString();
                    }

                    if (idShort == "SupportedProtocol")
                    {
                        result["SupportedProtocol"] = value?.ToString();
                    }
                }
            }
        }

        _logger.LogInformation("Technical Properties: {Properties}", JsonSerializer.Serialize(result));
        return result;
    }

    private bool IsAdapterCompatibleWithArticle(JsonObject article, JsonObject adapter)
    {
        var articleProperties = GetTechnicalProperties(article, isArticle: true);
        var adapterProperties = GetTechnicalProperties(adapter, isArticle: false);
        _logger.LogInformation("Article Properties: {ArticleProps}, Adapter Properties: {AdapterProps}", 
            JsonSerializer.Serialize(articleProperties), 
            JsonSerializer.Serialize(adapterProperties));

        // Check if both have SupportedProtocol and if they match
        if (articleProperties.TryGetValue("SupportedProtocol", out var articleProtocol) &&
            adapterProperties.TryGetValue("SupportedProtocol", out var adapterProtocol))
        {
            if (!articleProtocol.Equals(adapterProtocol))
            {
                _logger.LogInformation("SupportedProtocol mismatch: Article {ArticleProtocol}, Adapter {AdapterProtocol}", 
                    articleProtocol, adapterProtocol);
                return false;
            }
        }
        else
        {
            _logger.LogInformation("SupportedProtocol not found in either Article or Adapter");
            return false;
        }

        // Check if Article has ListOfHousingNumbers and Adapter has InsertSurface, and if any housing number matches
        if (articleProperties.TryGetValue("ListOfHousingNumbers", out var articleHousingNumbersJson) &&
            adapterProperties.TryGetValue("InsertSurface", out var adapterSurface))
        {
            var articleHousingNumbers = JsonSerializer.Deserialize<List<string>>(articleHousingNumbersJson);
            if (!articleHousingNumbers.Contains(adapterSurface))
            {
                _logger.LogInformation("No matching HousingNumber found: Article {ArticleHousingNumbers}, Adapter {AdapterSurface}", 
                    articleHousingNumbersJson, adapterSurface);
                return false;
            }
        }
        else
        {
            _logger.LogInformation("ListOfHousingNumbers or InsertSurface not found");
            return false;
        }

        _logger.LogInformation("Article and Adapter are compatible");
        return true;
    }

    private async Task<List<string>> GetMatchingDevices(List<string> deviceAssetIds, List<string> encodedDeviceAssetIds, JsonObject adapterAas)
    {
        var matchingDevices = new List<string>();

        for (int i = 0; i < encodedDeviceAssetIds.Count; i++)
        {
            var deviceAas = await GetAasFromAssetId(encodedDeviceAssetIds[i]);
            _logger.LogInformation("Device AAS: {DeviceAas}", GetSnippet(deviceAas.ToString()));

            if (IsDeviceCompatibleWithAdapter(adapterAas, deviceAas))
            {
                matchingDevices.Add(deviceAssetIds[i]);
            }
        }

        return matchingDevices;
    }

    private bool IsDeviceCompatibleWithAdapter(JsonObject adapter, JsonObject device)
    {
        var adapterProperties = GetDeviceCompatibilityProperties(adapter);
        var deviceProperties = GetDeviceCompatibilityProperties(device);
        _logger.LogInformation("Adapter Properties: {AdapterProps}, Device Properties: {DeviceProps}", 
            JsonSerializer.Serialize(adapterProperties), 
            JsonSerializer.Serialize(deviceProperties));

        return adapterProperties.Count == deviceProperties.Count &&
               adapterProperties.All(kvp => deviceProperties.TryGetValue(kvp.Key, out var value) && value.Equals(kvp.Value));
    }

    private Dictionary<string, string> GetDeviceCompatibilityProperties(JsonObject aas)
    {
        var result = new Dictionary<string, string>();

        var submodels = aas["submodels"] as JsonArray;

        // Get ElectricalConnectors version
        var interfaceConnectorsSubmodel = submodels?.FirstOrDefault(sm => sm?["idShort"]?.ToString() == "InterfaceConnectors") as JsonObject;
        if (interfaceConnectorsSubmodel != null)
        {
            var submodelElements = interfaceConnectorsSubmodel["submodelElements"] as JsonArray;
            var electricalConnectors = submodelElements?.FirstOrDefault(sme => sme?["idShort"]?.ToString() == "ElectricalConnectors") as JsonObject;

            if (electricalConnectors != null)
            {
                var electricalInterfaceVersion = electricalConnectors["value"]?
                    .AsArray()?.FirstOrDefault(v => v?["idShort"]?.ToString() == "ElectricalInterfaceVersion");

                if (electricalInterfaceVersion != null)
                {
                    result["ElectricalConnectors"] = electricalInterfaceVersion["value"]?.ToString();
                }
            }
        }

        // Get HardwareVersion
        var nameplateSubmodel = submodels?.FirstOrDefault(sm => sm?["idShort"]?.ToString() == "Nameplate") as JsonObject;
        if (nameplateSubmodel != null)
        {
            var submodelElements = nameplateSubmodel["submodelElements"] as JsonArray;
            var hardwareVersion = submodelElements?.FirstOrDefault(sme => sme?["idShort"]?.ToString() == "HardwareVersion") as JsonObject;

            if (hardwareVersion != null)
            {
                var versionValue = hardwareVersion["value"] as JsonArray;
                var englishVersion = versionValue?.FirstOrDefault(v => v?["language"]?.ToString() == "en");

                if (englishVersion != null)
                {
                    result["HardwareVersion"] = englishVersion["text"]?.ToString();
                }
            }
        }

        _logger.LogInformation("Device Compatibility Properties: {Properties}", JsonSerializer.Serialize(result));
        return result;
    }

    private string Base64UrlEncode(string input)
    {
        var inputBytes = Encoding.UTF8.GetBytes(input);
        return Convert.ToBase64String(inputBytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private string GetSnippet(string content, int length = 300)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        return content.Length <= length ? content : content.Substring(0, length) + "...";
    }

    public class MatchResult
    {
        public AssetIdWrapper Asset { get; set; }
        public List<AdapterDevicePairing> AdapterDevicePairings { get; set; }
    }

    public class PartialMatchResult
    {
        public AssetIdWrapper Asset { get; set; }
        public List<TestAdapter> Adapters { get; set; }
    }

    public class AssetIdWrapper
    {
        public AssetId ArticleAssetId { get; set; }
    }

    public class AdapterDevicePairing
    {
        public TestAdapter TestAdapter { get; set; }
        public List<AssetId> TestDeviceAssetIds { get; set; }
    }

    public class TestAdapter
    {
        public AssetId TestAdapterAssetId { get; set; }
        public string TestAdapterType { get; set; }
        public string InsertSurface { get; set; }
    }

    public class AssetId
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("value")]
        public string Value { get; set; }

        public static AssetId FromJson(string json)
        {
            try
            {
                var jsonElement = JsonSerializer.Deserialize<JsonElement>(json);
                return new AssetId
                {
                    Name = jsonElement.GetProperty("name").GetString(),
                    Value = jsonElement.GetProperty("value").GetString()
                };
            }
            catch
            {
                // If parsing fails, return an AssetId with the original string as both name and value
                return new AssetId { Name = json, Value = json };
            }
        }
    }
}