using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System;
using System.Text;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.Json.Serialization;

[ApiController]
[Route("[controller]")]
public class AASLookupController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;

    public AASLookupController(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

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

            var registryResponse = await registryClient.GetAsync($"http://aas-registry-v3:8080/api/v3.0/shell-descriptors/{encodedAasId}");
            registryResponse.EnsureSuccessStatusCode();
            var registryContent = await registryResponse.Content.ReadAsStringAsync();
            var registryResult = JsonDocument.Parse(registryContent);

            if (!registryResult.RootElement.TryGetProperty("endpoints", out var endpoints) || endpoints.GetArrayLength() == 0)
            {
                Console.WriteLine($"No endpoints found for AAS ID: {aasId}");
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
                            Console.WriteLine("Fetched Submodel Data: " + submodelData.GetRawText());
                            aasDataWrapper.Submodels.Add(submodelData);
                        }
                    }
                }

                // Log the aasDataWrapper before serialization
                try
                {
                    var aasDataJson = JsonSerializer.Serialize(aasDataWrapper);
                    Console.WriteLine("AAS Data Wrapper: " + aasDataJson);
                    var aasDataElement = JsonDocument.Parse(aasDataJson).RootElement;
                    aasDataList.Add(aasDataElement);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error serializing aasDataWrapper: {ex.Message}");
                }
            }
        }

        return Ok(aasDataList);
    }

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
            Console.WriteLine($"Failed to fetch AAS data from URL: {ex.Message}");
            return new JsonElement();
        }
    }

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
            Console.WriteLine($"Submodel {url} does not exist");
            return default;
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Failed to fetch submodel data from URL: {ex.Message}");
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
