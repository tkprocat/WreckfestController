using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using WreckfestController.Controllers;
using WreckfestController.Tests.Services.Catalogue;

namespace WreckfestController.Tests.Api;

/// <summary>POST /api/catalogue/import/trackdump: a WreckfestTrackDump tracks.json uploaded by an admin (#121).</summary>
public class CatalogueImportEndpointsTests
{
    private const string Url = "/api/catalogue/import/trackdump";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TrackDumpFixture Pack() => new TrackDumpFixture()
        .Workshop("2276098616", "The Very TrackPack 2")
        .Track("workshop:2276098616", "asan", "Asan Circuit", ("asan_main", "Main", 1, 3912.5), ("asan_arena", "Arena", 4, 21120))
        .Diagnostic("missing-value", "A blank description.");

    [Fact]
    public async Task Anonymous_CannotImport()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateClient();

        using var response = await PostAsync(client, Pack().ToJson());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var admin = host.CreateAuthenticatedClient();
        Assert.Empty(await VariantsAsync(admin, "asan_main"));
    }

    [Fact]
    public async Task Import_AddsTheTracks_AndReportsWhatItDid()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        using var response = await PostAsync(client, Pack().ToJson());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(1, report.GetProperty("mods").GetProperty("added").GetInt32());
        Assert.Equal(1, report.GetProperty("tracks").GetProperty("added").GetInt32());
        Assert.Equal(2, report.GetProperty("variants").GetProperty("added").GetInt32());
        Assert.Equal("missing-value", report.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        Assert.Equal("WreckfestTrackDump 1.0.0", report.GetProperty("tool").GetString());

        var variant = Assert.Single(await VariantsAsync(client, "asan_main"));
        Assert.Equal(3912.5, variant.GetProperty("trackLengthM").GetDouble());
        Assert.False(variant.GetProperty("isBuiltIn").GetBoolean());
        Assert.False(variant.GetProperty("allowedForVoting").GetBoolean());

        // The dump's local source paths are not kept or echoed.
        Assert.DoesNotContain("C:/Steam", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        using var again = await PostAsync(client, Pack().ToJson());
        var second = await again.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(2, second.GetProperty("variants").GetProperty("unchanged").GetInt32());
        Assert.Equal(0, second.GetProperty("variants").GetProperty("added").GetInt32());
    }

    [Fact]
    public async Task ABuiltInVariant_KeepsItsValues_AndGetsItsLength()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        var before = Assert.Single(await VariantsAsync(client, "bigstadium_figure_8"));

        using var response = await PostAsync(client, new TrackDumpFixture().Base()
            .Track("base", "bigstadium", "Big Stadium", ("bigstadium_figure_8", "Renamed by the dump", 4, 640)).ToJson());
        response.EnsureSuccessStatusCode();

        var after = Assert.Single(await VariantsAsync(client, "bigstadium_figure_8"));
        foreach (var field in new[] { "name", "gameMode", "trackKey", "allowedForVoting", "isBuiltIn" })
        {
            Assert.Equal(before.GetProperty(field).ToString(), after.GetProperty(field).ToString());
        }
    }

    [Fact]
    public async Task AnUnknownFormatVersion_IsRefusedWith400()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        var newer = new TrackDumpFixture { FormatVersion = 2 }.Base().Track("base", "x", "X", ("x_main", "Main", 1, 10));

        using var response = await PostAsync(client, newer.ToJson());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Contains("formatVersion 2", problem.GetProperty("title").GetString());
        Assert.Empty(await VariantsAsync(client, "x_main"));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("\"tracks\"")]
    public async Task WhatIsNotATrackDump_IsA400(string body)
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();

        using var response = await PostAsync(client, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnUploadOverTheLimit_IsRefusedBeforeItIsRead()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateAuthenticatedClient();
        var padding = new string(' ', (int)CatalogueImportController.MaxBodyBytes);

        using var response = await PostAsync(client, Pack().ToJson() + padding);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(await VariantsAsync(client, "asan_main"));
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string json) =>
        client.PostAsync(Url, new StringContent(json, Encoding.UTF8, "application/json"), Ct);

    private static async Task<List<JsonElement>> VariantsAsync(HttpClient client, string variantId)
    {
        var json = await client.GetFromJsonAsync<JsonElement>($"/api/catalogue/variants?includeHidden=true&search={variantId}", Ct);
        return json.EnumerateArray().Where(v => v.GetProperty("variantId").GetString() == variantId).ToList();
    }
}
