using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System;
using System.Text;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

[ApiController]
[Route("[controller]")]
public class AASLookupController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AASLookupController> _logger;

    public AASLookupController(IHttpClientFactory httpClientFactory, ILogger<AASLookupController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Looks up Asset Administration Shell(s) by Specific Asset ID
    /// </summary>
    /// <param name="assetId">The specific asset Id as a name/value pair object, base64-encoded</param>
    /// <param name="submodels">If true, includes submodel data in the response</param>
    /// <returns>The Administration Shells matching the given Specific Asset ID</returns>
    /// <response code="200">Returns the matching Asset Administration Shells</response>
    /// <response code="400">If the spefific asset-id query parameter is missing</response>
    /// <response code="404">If no matching Asset Administration Shells are found</response>
    /// <remarks>
    /// Sample Specific Asset Id:
    /// 
    /// {"name": "PG210","value": "AssetPG210_Value"} --> Base64-encoding --> eyJuYW1lIjogIlBHMjEwIiwidmFsdWUiOiAiQXNzZXRQRzIxMF9WYWx1ZSJ9
    /// </remarks>
    [HttpGet("lookup")]
    public async Task<IActionResult> LookupAASByAssetId([FromQuery] string assetId, [FromQuery] bool submodels = false)
    {
        if (string.IsNullOrEmpty(assetId))
        {
            return BadRequest("asset-id query parameter is required");
        }

        var discoveryClient = _httpClientFactory.CreateClient();
        var registryClient = _httpClientFactory.CreateClient();
        
        var discoveryRequest = new HttpRequestMessage(HttpMethod.Get, $"http://aas-discovery-service:8081/lookup/shells?assetIds={assetId}");
        
        var discoveryResponse = await discoveryClient.SendAsync(discoveryRequest);
        discoveryResponse.EnsureSuccessStatusCode();
        var discoveryContent = await discoveryResponse.Content.ReadAsStringAsync();
        var discoveryResult = JsonDocument.Parse(discoveryContent);

        var matchingAasIds = new List<string>();
        foreach (var aasIdElement in discoveryResult.RootElement.GetProperty("result").EnumerateArray())
        {
            matchingAasIds.Add(aasIdElement.GetString());
        }

        var aasDataList = new List<JsonElement>();

        foreach (var aasId in matchingAasIds)
        {
            var encodedAasId = Base64UrlEncode(aasId);

            var registryResponse = await registryClient.GetAsync($"http://aas-registry-v3:8080/shell-descriptors/{encodedAasId}");
            registryResponse.EnsureSuccessStatusCode();
            var registryContent = await registryResponse.Content.ReadAsStringAsync();
            var registryResult = JsonDocument.Parse(registryContent);

            if (!registryResult.RootElement.TryGetProperty("endpoints", out var endpoints) || endpoints.GetArrayLength() == 0)
            {
                _logger.LogInformation($"No endpoints found for AAS ID: {aasId}");
                continue;
            }

            var aasEndpointUrl = endpoints[0].GetProperty("protocolInformation").GetProperty("href").GetString();
            var internalAasEndpointUrl = ConvertToInternalUrl(aasEndpointUrl);

            var aasData = await FetchAASData(registryClient, internalAasEndpointUrl);

            if (aasData.ValueKind != JsonValueKind.Null)
            {
                var aasDataWrapper = new JsonObjectWrapper
                {
                    AssetAdministrationShells = new List<JsonElement> { aasData },
                    Submodels = new List<JsonElement>()
                };

                if (submodels)
                {
                    var submodelRefsResponse = await registryClient.GetAsync($"{internalAasEndpointUrl}/submodel-refs");
                    submodelRefsResponse.EnsureSuccessStatusCode();
                    var submodelRefsContent = await submodelRefsResponse.Content.ReadAsStringAsync();
                    var submodelRefsResult = JsonDocument.Parse(submodelRefsContent);

                    foreach (var submodelRef in submodelRefsResult.RootElement.GetProperty("result").EnumerateArray())
                    {
                        var submodelId = submodelRef.GetProperty("keys")[0].GetProperty("value").GetString();
                        var encodedSubmodelId = Base64UrlEncode(submodelId);
                        var submodelUrl = $"http://aas-environment-v3:8081/submodels/{encodedSubmodelId}";

                        var submodelData = await FetchSubmodelData(registryClient, submodelUrl);

                        if (submodelData.ValueKind != JsonValueKind.Null)
                        {
                            _logger.LogInformation("Fetched Submodel Data: {DataSnippet}", GetSnippet(submodelData.GetRawText()));
                            aasDataWrapper.Submodels.Add(submodelData);
                        }
                    }
                }

                // Log the aasDataWrapper before serialization
                try
                {
                    var aasDataJson = JsonSerializer.Serialize(aasDataWrapper);
                    _logger.LogInformation("AAS Data Wrapper: {DataSnippet}", GetSnippet(aasDataJson));
                    var aasDataElement = JsonDocument.Parse(aasDataJson).RootElement;
                    _logger.LogInformation("AAS Data Element: {DataSnippet}", GetSnippet(aasDataElement.GetRawText()));
                    aasDataList.Add(aasDataElement);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error serializing aasDataWrapper: {ex.Message}");
                }
            }
        }

        _logger.LogInformation("AAS Data List: {DataSnippet}", GetSnippet(string.Join(", ", aasDataList.Select(a => a.GetRawText()))));
        return Ok(aasDataList);
    }

    /// <summary>
    /// Fetches Asset Administration Shell data from a given URL
    /// </summary>
    /// <param name="client">The HttpClient to use for the request</param>
    /// <param name="url">The URL to fetch the AAS data from</param>
    /// <returns>The AAS data as a JsonElement</returns>
    private async Task<JsonElement> FetchAASData(HttpClient client, string url)
    {
        try
        {
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            return JsonDocument.Parse(content).RootElement;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError($"Failed to fetch AAS data from URL: {GetSnippet(ex.Message)}");
            return new JsonElement();
        }
    }

    /// <summary>
    /// Fetches Submodel data from a given URL
    /// </summary>
    /// <param name="client">The HttpClient to use for the request</param>
    /// <param name="url">The URL to fetch the Submodel data from</param>
    /// <returns>The Submodel data as a JsonElement, or default if not found</returns>
    private async Task<JsonElement> FetchSubmodelData(HttpClient client, string url)
    {
        try
        {
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            return JsonDocument.Parse(content).RootElement;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogInformation($"Submodel {url} does not exist");
            return default;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError($"Failed to fetch submodel data from URL: {GetSnippet(ex.Message)}");
            return default;
        }
    }

    private string ConvertToInternalUrl(string url)
    {
        return url.Replace("localhost:8082", "aas-environment-v3:8081");
    }

    private string Base64UrlEncode(string input)
    {
        var byteArray = Encoding.UTF8.GetBytes(input);
        var base64 = Convert.ToBase64String(byteArray);
        return base64.Replace("+", "-").Replace("/", "_").Replace("=", "");
    }

    private string GetSnippet(string content, int length = 300)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        return content.Length <= length ? content : content.Substring(0, length) + "...";
    }
}

public class JsonObjectWrapper
{
    [JsonPropertyName("assetAdministrationShells")]
    public List<JsonElement> AssetAdministrationShells { get; set; }

    [JsonPropertyName("submodels")]
    public List<JsonElement> Submodels { get; set; }

    [JsonPropertyName("conceptDescriptions")]
    public List<JsonElement> ConceptDescriptions { get; set; } = new List<JsonElement>();
}
