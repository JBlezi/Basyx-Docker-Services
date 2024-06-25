using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("AASPostService")]
public class AASPostController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;

    public AASPostController(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    [HttpPost("uploadJSON")]
    public async Task<IActionResult> UploadJSON(IFormFile jsonFile, bool register = true, bool discover = true)
    {
        if (jsonFile == null || jsonFile.Length == 0)
        {
            return BadRequest("JSON file is required.");
        }

        using (var stream = jsonFile.OpenReadStream())
        {
            using (var reader = new StreamReader(stream))
            {
                var jsonContent = await reader.ReadToEndAsync();
                return await ProcessJSONContent(jsonContent, register, discover);
            }
        }
    }

    private async Task<IActionResult> ProcessJSONContent(string jsonContent, bool register, bool discover)
    {
        JsonDocument jsonDocument;
        try
        {
            jsonDocument = JsonDocument.Parse(jsonContent);
        }
        catch (JsonException ex)
        {
            return BadRequest($"Invalid JSON file: {ex.Message}");
        }

        var root = jsonDocument.RootElement;

        if (!root.TryGetProperty("assetAdministrationShells", out var assetAdministrationShells) ||
            !root.TryGetProperty("submodels", out var submodels) ||
            !root.TryGetProperty("conceptDescriptions", out var conceptDescriptions))
        {
            return BadRequest("Invalid JSON structure.");
        }

        // Log the JSON parts
        Console.WriteLine($"Asset Administration Shells: {assetAdministrationShells}");
        // Console.WriteLine($"Submodels: {submodels}");
        // Console.WriteLine($"Concept Descriptions: {conceptDescriptions}");

        var repoClient = _httpClientFactory.CreateClient();

        // Step 1: Post Asset Administration Shells
        foreach (var shell in assetAdministrationShells.EnumerateArray())
        {
            var shellContent = new StringContent(shell.GetRawText(), Encoding.UTF8, "application/json");
            var shellsResponse = await repoClient.PostAsync("http://aas-environment-v3:8081/shells", shellContent);

            if (!shellsResponse.IsSuccessStatusCode)
            {
                if (shellsResponse.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    Console.WriteLine($"Asset Administration Shell already exists: {await shellsResponse.Content.ReadAsStringAsync()}");
                }
                else
                {
                    return StatusCode((int)shellsResponse.StatusCode, $"Failed to post Asset Administration Shell: {await shellsResponse.Content.ReadAsStringAsync()}");
                }
            }
        }

        // Step 2: Post Submodels
        int submodelIndex = 1;
        foreach (var submodel in submodels.EnumerateArray())
        {
            var submodelContent = new StringContent(submodel.GetRawText(), Encoding.UTF8, "application/json");

            Console.WriteLine($"Submodel {submodelIndex}: {submodelContent}");

            var submodelsResponse = await repoClient.PostAsync("http://aas-environment-v3:8081/submodels", submodelContent);

            if (!submodelsResponse.IsSuccessStatusCode)
            {
                if (submodelsResponse.StatusCode == System.Net.HttpStatusCode.Conflict)
                {
                    Console.WriteLine($"Submodel {submodelIndex} already exists.");
                }
                else
                {
                    return StatusCode((int)submodelsResponse.StatusCode, $"Failed to post Submodel {submodelIndex}: {await submodelsResponse.Content.ReadAsStringAsync()}");
                }
            }
            else
            {
                Console.WriteLine($"Submodel {submodelIndex} successfully posted.");
            }

            submodelIndex++;
        }

        if (register)
        {
            foreach (var shell in assetAdministrationShells.EnumerateArray())
            {
                string aasId = null;
                string administrationRevision = null;
                string assetKind = null;
                string idShort = null;
                List<SpecificAssetId> specificAssetIds = null;

                // Check and extract properties safely
                if (shell.TryGetProperty("id", out var idElement))
                {
                    aasId = idElement.GetString();
                }

                if (shell.TryGetProperty("administration", out var administrationElement) &&
                    administrationElement.TryGetProperty("revision", out var revisionElement))
                {
                    administrationRevision = revisionElement.GetString();
                }

                if (shell.TryGetProperty("assetInformation", out var assetInfoElement))
                {
                    if (assetInfoElement.TryGetProperty("assetKind", out var assetKindElement))
                    {
                        assetKind = assetKindElement.GetString();
                    }

                    if (assetInfoElement.TryGetProperty("specificAssetIds", out var specificAssetIdsElement))
                    {
                        specificAssetIds = specificAssetIdsElement.EnumerateArray()
                            .Select(id => new SpecificAssetId
                            {
                                Name = id.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null,
                                Value = id.TryGetProperty("value", out var valueElement) ? valueElement.GetString() : null
                            }).ToList();
                    }
                }

                if (shell.TryGetProperty("idShort", out var idShortElement))
                {
                    idShort = idShortElement.GetString();
                }

                // Construct registry entry
                var registryEntry = new RegistryEntry
                {
                    IdShort = idShort,
                    Id = aasId,
                    AssetKind = assetKind,
                    Endpoints = new List<Endpoint>
                    {
                        new Endpoint
                        {
                            ProtocolInformation = new ProtocolInformation
                            {
                                Href = $"http://aas-environment-v3:8081/shells/{Base64UrlEncode(aasId)}",
                                EndpointProtocol = "HTTP",
                                Subprotocol = "AAS"
                            },
                            Interface = "https://admin-shell.io/aas/API/3/0/AasServiceSpecification/SSP-003"
                        }
                    },
                    Description = new List<Description>
                    {
                        new Description
                        {
                            Language = "en-US",
                            Text = "Standardized digital representation of Murrelektroniks. It holds digital models of various aspects (submodels) and describes technical functionality exposed by them."
                        }
                    }
                };

                // Include administration if available
                if (administrationRevision != null)
                {
                    registryEntry.Administration = new Administration { Revision = administrationRevision };
                }

                // Log the registry entry before sending
                var registryEntryJson = JsonSerializer.Serialize(registryEntry);
                Console.WriteLine($"Registry Entry JSON: {registryEntryJson}");

                // Step 4: Upload the registry entry
                var registryClient = _httpClientFactory.CreateClient();
                var registryContent = new StringContent(JsonSerializer.Serialize(registryEntry), Encoding.UTF8, "application/json");
                try
                {
                    var registryResponse = await registryClient.PostAsync($"http://aas-registry-v3:8080/api/v3.0/shell-descriptors", registryContent);
                    var registryResponseContent = await registryResponse.Content.ReadAsStringAsync();
                    var registryResponseHeaders = registryResponse.Headers.ToString();
                    Console.WriteLine($"Registry Response: {registryResponse.StatusCode}, {registryResponseContent}");
                    Console.WriteLine($"Registry Response Headers: {registryResponseHeaders}");

                    if (!registryResponse.IsSuccessStatusCode)
                    {
                        return StatusCode((int)registryResponse.StatusCode, $"Failed to register AAS: {registryResponseContent}");
                    }
                }
                catch (HttpRequestException ex)
                {
                    Console.WriteLine($"An error occurred while posting to the registry: {ex.Message}");
                    return StatusCode(500, $"An error occurred while posting to the registry: {ex.Message}");
                }

                // Step 5: If discover is true, construct and upload the discovery entry
                if (discover)
                {
                    var discoveryClient = _httpClientFactory.CreateClient();

                    // Modify discoveryEntry to match the expected JSON structure
                    var discoveryEntry = specificAssetIds;

                    var discoveryContent = new StringContent(JsonSerializer.Serialize(discoveryEntry), Encoding.UTF8, "application/json");
                    var discoveryEntryJson = JsonSerializer.Serialize(discoveryEntry);
                    Console.WriteLine($"Discovery Content JSON: {discoveryEntryJson}"); // Log the discovery content JSON

                    // Create the request with the necessary headers
                    var request = new HttpRequestMessage(HttpMethod.Post, $"http://aas-discovery-service:8081/lookup/shells/{Base64UrlEncode(aasId)}")
                    {
                        Content = discoveryContent
                    };
                    request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                    discoveryContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

                    var discoveryResponse = await discoveryClient.SendAsync(request);

                    var discoveryResponseContent = await discoveryResponse.Content.ReadAsStringAsync();
                    var discoveryResponseHeaders = discoveryResponse.Headers.ToString();
                    Console.WriteLine($"Discovery Response: {discoveryResponse.StatusCode}, {discoveryResponseContent}"); // Log the discovery response
                    Console.WriteLine($"Discovery Response Headers: {discoveryResponseHeaders}");

                    if (!discoveryResponse.IsSuccessStatusCode)
                    {
                        return StatusCode((int)discoveryResponse.StatusCode, $"Failed to link in Discovery: {discoveryResponseContent}");
                    }
                }
            }
            return Ok("JSON uploaded, registered and linked in Discovery.");
        }
        else
        {
            return Ok("JSON uploaded.");
        }
    }

    private string Base64UrlEncode(string input)
    {
        var byteArray = Encoding.UTF8.GetBytes(input);
        var base64 = Convert.ToBase64String(byteArray);
        return base64.Replace("+", "-").Replace("/", "_").Replace("=", "");
    }

    public class ShellDescriptor
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }
    
        [JsonPropertyName("administration")]
        public Administration Administration { get; set; }
    
        [JsonPropertyName("assetInformation")]
        public AssetInformation AssetInformation { get; set; }
    
        [JsonPropertyName("idShort")]
        public string IdShort { get; set; }
    }

    public class Administration
    {
        [JsonPropertyName("revision")]
        public string Revision { get; set; }
    }

    public class AssetInformation
    {
        [JsonPropertyName("assetKind")]
        public string AssetKind { get; set; }
    
        [JsonPropertyName("globalAssetId")]
        public string GlobalAssetId { get; set; }
    
        [JsonPropertyName("specificAssetIds")]
        public List<SpecificAssetId> SpecificAssetIds { get; set; }
    }

    public class SpecificAssetId
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }
    
        [JsonPropertyName("value")]
        public string Value { get; set; }
    }

    public class ShellsResponse
    {
        [JsonPropertyName("paging_metadata")]
        public PagingMetadata PagingMetadata { get; set; }

        [JsonPropertyName("result")]
        public List<ShellDescriptor> Result { get; set; }
    }

    public class PagingMetadata
    {
        // Define properties for paging metadata if needed
    }

    public class RegistryEntry
    {
        [JsonPropertyName("idShort")]
        public string IdShort { get; set; }
    
        [JsonPropertyName("id")]
        public string Id { get; set; }
    
        [JsonPropertyName("assetKind")]
        public string AssetKind { get; set; }
    
        [JsonPropertyName("administration")]
        public Administration Administration { get; set; }
    
        [JsonPropertyName("endpoints")]
        public List<Endpoint> Endpoints { get; set; }
    
        [JsonPropertyName("description")]
        public List<Description> Description { get; set; }
    }

    public class Endpoint
    {
        [JsonPropertyName("protocolInformation")]
        public ProtocolInformation ProtocolInformation { get; set; }
    
        [JsonPropertyName("interface")]
        public string Interface { get; set; }
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

    public class Description
    {
        [JsonPropertyName("language")]
        public string Language { get; set; }
    
        [JsonPropertyName("text")]
        public string Text { get; set; }
    }
}
