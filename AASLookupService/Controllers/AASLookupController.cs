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
    public async Task<IActionResult> LookupAASByAssetId([FromQuery] string assetId)
    {
        if (string.IsNullOrEmpty(assetId))
        {
            return BadRequest("asset-id query parameter is required");
        }

        var discoveryClient = _httpClientFactory.CreateClient();
        var registryClient = _httpClientFactory.CreateClient();
        
        // var encodedAssetId = Base64UrlEncode(assetId);

        var discoveryRequest = new HttpRequestMessage(HttpMethod.Get, $"http://aas-discovery-service:8081/lookup/shells?assetIds={assetId}");
        // discoveryRequest.Headers.Add("Accept", "application/json");

        // Query the Discovery Service for all AAS IDs
        var discoveryResponse = await discoveryClient.SendAsync(discoveryRequest);
        discoveryResponse.EnsureSuccessStatusCode();
        var discoveryContent = await discoveryResponse.Content.ReadAsStringAsync();
        var discoveryResult = JsonSerializer.Deserialize<DiscoveryResponse>(discoveryContent);

        var matchingAasIds = new List<string>();

        // Query the Discovery Service for each individual AAS ID
        foreach (var aasId in discoveryResult.Result)
        {
            /*
            // Base64-URL-encode the AAS ID
            var encodedAasId = Base64UrlEncode(aasId);

            var individualDiscoveryRequest = new HttpRequestMessage(HttpMethod.Get, $"http://aas-discovery-service:8081/lookup/shells/{encodedAasId}");
            individualDiscoveryRequest.Headers.Add("Accept", "application/json");

            var discoveryResponseIndividual = await discoveryClient.SendAsync(individualDiscoveryRequest);
            discoveryResponseIndividual.EnsureSuccessStatusCode();
            var discoveryContentIndividual = await discoveryResponseIndividual.Content.ReadAsStringAsync();

            var discoveryResultIndividual = JsonSerializer.Deserialize<List<AssetItem>>(discoveryContentIndividual);

            // Check if the asset ID matches
            if (discoveryResultIndividual.Any(asset => asset.Value == assetId))
            {
                matchingAasIds.Add(aasId);
            }
            */
            matchingAasIds.Add(aasId);
        }
        
                
        var aasDataList = new List<AASData>();


        // Query the Registry Service for each matching AAS ID
        foreach (var aasId in matchingAasIds)
        {
            var encodedAasId = Base64UrlEncode(aasId);

            var registryResponse = await registryClient.GetAsync($"http://aas-registry-v3:8080/api/v3.0/shell-descriptors/{encodedAasId}");
            registryResponse.EnsureSuccessStatusCode();
            var registryContent = await registryResponse.Content.ReadAsStringAsync();

            Console.WriteLine($"RegistryContent: {registryContent}");  // Log the content to verify

            var registryResult = JsonSerializer.Deserialize<RegistryResponse>(registryContent);
            Console.WriteLine($"registryResult: {registryResult}");  // Log the content to verify

            Console.WriteLine($"registryResult.Endpoints: {registryResult.Endpoints}");  // Log the content to verify

            if (registryResult.Endpoints == null || !registryResult.Endpoints.Any())
            {
                Console.WriteLine($"No endpoints found for AAS ID: {aasId}");
                continue;
            }

            var aasEndpointUrl = registryResult.Endpoints.FirstOrDefault()?.ProtocolInformation?.Href;
            Console.WriteLine($"AAS Endpoint URL: {aasEndpointUrl}");  // Log the content to verify


             // Replace the external host and port with the internal host and port
            var internalAasEndpointUrl = ConvertToInternalUrl(aasEndpointUrl);

            // Attempt to connect to the AAS endpoint using the internal URL
            var aasData = await FetchAASData(registryClient, internalAasEndpointUrl);

            Console.WriteLine($"Final AASData: {aasData}");

            aasDataList.Add(aasData);
        }

        return Ok(aasDataList);
    }

    private async Task<AASData> FetchAASData(HttpClient client, string url)
    {
        try
        {
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<AASData>(content);
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Failed to fetch AAS data from URL: {ex.Message}");
            return null;
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

public class DiscoveryResponse
{
    [JsonPropertyName("paging_metadata")]
    public PagingMetadata PagingMetadata { get; set; }
    
    [JsonPropertyName("result")]
    public List<string> Result { get; set; }
}

public class PagingMetadata
{
    // Define properties for paging metadata if needed
}


public class DiscoveryIndividualResponse
{
    public List<AssetItem> Assets { get; set; }
}

public class AssetItem
{
    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("value")]
    public string Value { get; set; }
}


public class RegistryResponse
{
    [JsonPropertyName("description")]
    public List<Description> Description { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("administration")]
    public Administration Administration { get; set; }

    [JsonPropertyName("assetKind")]
    public string AssetKind { get; set; }

    [JsonPropertyName("endpoints")]
    public List<Endpoint> Endpoints { get; set; }

    [JsonPropertyName("idShort")]
    public string IdShort { get; set; }
}

public class Description
{
    [JsonPropertyName("language")]
    public string Language { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; }
}

public class Administration
{
    [JsonPropertyName("revision")]
    public string Revision { get; set; }
}

public class Endpoint
{
    [JsonPropertyName("interface")]
    public string Interface { get; set; }

    [JsonPropertyName("protocolInformation")]
    public ProtocolInformation ProtocolInformation { get; set; }
}

public class ProtocolInformation
{
    [JsonPropertyName("href")]
    public string Href { get; set; }

    [JsonPropertyName("endpointProtocol")]
    public string EndpointProtocol { get; set; }

    [JsonPropertyName("subprotocol")]
    public string Subprotocol { get; set; }
}

public class AASData
{
    [JsonPropertyName("modelType")]
    public string ModelType { get; set; }

    [JsonPropertyName("assetInformation")]
    public AssetInformation AssetInformation { get; set; }

    [JsonPropertyName("submodels")]
    public List<Submodel> Submodels { get; set; }

    [JsonPropertyName("administration")]
    public Administration Administration { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("description")]
    public List<Description> Description { get; set; }

    [JsonPropertyName("displayName")]
    public List<DisplayName> DisplayName { get; set; }

    [JsonPropertyName("idShort")]
    public string IdShort { get; set; }
}

public class AssetInformation
{
    [JsonPropertyName("assetKind")]
    public string AssetKind { get; set; }

    [JsonPropertyName("defaultThumbnail")]
    public DefaultThumbnail DefaultThumbnail { get; set; }

    [JsonPropertyName("globalAssetId")]
    public string GlobalAssetId { get; set; }
}

public class DefaultThumbnail
{
    [JsonPropertyName("contentType")]
    public string ContentType { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; }
}

public class Submodel
{
    [JsonPropertyName("keys")]
    public List<Key> Keys { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; }
}

public class Key
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("value")]
    public string Value { get; set; }
}

public class DisplayName
{
    [JsonPropertyName("language")]
    public string Language { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; }
}