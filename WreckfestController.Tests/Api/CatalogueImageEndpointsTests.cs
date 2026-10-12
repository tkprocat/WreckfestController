using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using WreckfestController.Tests.Services.Catalogue;

namespace WreckfestController.Tests.Api;

/// <summary>
/// #260: track and variant images over HTTP. Admins import them with a dump and upload
/// their own; anyone may fetch them, with caching headers.
/// </summary>
public class CatalogueImageEndpointsTests
{
    private const string ImportUrl = "/api/catalogue/import/trackdump/images";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TrackDumpFixture BaseGame() => new TrackDumpFixture()
        .Base()
        .Track("base", "bigstadium", "Madman Stadium", ("bigstadium_figure_8", "Figure 8", 1, 800))
        .Images("base/bigstadium", "env_bigstadium_small", "env_bigstadium_large");

    [Fact]
    public async Task ImportedImages_AreServedToAnyone_WithAnETag()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var admin = host.CreateAuthenticatedClient();

        using var imported = await ImportAsync(admin, BaseGame(), ("dump/base/env_bigstadium_large.png", TestImages.Png(2)));
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var report = await imported.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(1, report.GetProperty("stored").GetInt32());

        using var anonymous = host.CreateClient();
        using var image = await anonymous.GetAsync("/api/images/tracks/madman_stadium/large", Ct);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TestImages.Png(2), await image.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(["nosniff"], image.Headers.GetValues("X-Content-Type-Options"));
        Assert.Equal(TimeSpan.FromMinutes(5), image.Headers.CacheControl?.MaxAge);
        var etag = image.Headers.ETag!;

        // The small size falls back to the large one, and a variant to its track.
        using var small = await anonymous.GetAsync("/api/images/variants/BIGSTADIUM_FIGURE_8/small", Ct);
        Assert.Equal(TestImages.Png(2), await small.Content.ReadAsByteArrayAsync(Ct));

        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/api/images/tracks/madman_stadium/large");
        conditional.Headers.IfNoneMatch.Add(etag);
        using var notModified = await anonymous.SendAsync(conditional, Ct);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);

        // A link naming the image's hash may be cached for good.
        using var versioned = await anonymous.GetAsync($"/api/images/tracks/madman_stadium/large?v={etag.Tag.Trim('"')}", Ct);
        Assert.Equal(TimeSpan.FromDays(365), versioned.Headers.CacheControl?.MaxAge);
    }

    [Fact]
    public async Task AVariantsOutline_IsServedAsItsLayout()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var admin = host.CreateAuthenticatedClient();
        var dump = BaseGame().Images("bigstadium_figure_8", "event_bigstadium_figure_8_small", null);
        (await ImportAsync(admin, dump, ("base/event_bigstadium_figure_8_small.png", TestImages.Outline(30, 14)))).Dispose();

        using var anonymous = host.CreateClient();
        using var layout = await anonymous.GetAsync("/api/images/variants/bigstadium_figure_8/layout", Ct);
        using var picture = await anonymous.GetAsync("/api/images/variants/bigstadium_figure_8/small", Ct);

        Assert.Equal(TestImages.Outline(30, 14), await layout.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal(HttpStatusCode.NotFound, picture.StatusCode);
        using var listed = await admin.GetAsync("/api/catalogue/images", Ct);
        Assert.True((await listed.Content.ReadFromJsonAsync<JsonElement>(Ct))[0].GetProperty("isOverlay").GetBoolean());
    }

    [Theory]
    [InlineData("/api/images/tracks/madman_stadium/small")]
    [InlineData("/api/images/variants/bigstadium_figure_8/layout")]
    [InlineData("/api/images/tracks/nowhere/large")]
    [InlineData("/api/images/variants/nowhere/large")]
    public async Task NoImage_Is404(string url)
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("loading")]
    [InlineData("7")]
    public async Task ASizeTheCatalogueDoesNotKeep_IsABadRequest(string size)
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateClient();

        using var response = await client.GetAsync($"/api/images/tracks/madman_stadium/{size}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_CannotImportUploadOrList()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var client = host.CreateClient();
        var trackId = await TrackIdAsync(host, "madman_stadium");

        using var import = await ImportAsync(client, BaseGame(), ("base/env_bigstadium_large.png", TestImages.Png(2)));
        using var upload = await UploadAsync(client, $"/api/catalogue/tracks/{trackId}/images/large", TestImages.Png(1));
        using var list = await client.GetAsync("/api/catalogue/images", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, import.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, upload.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
    }

    [Fact]
    public async Task AnUpload_WinsOverTheImport_UntilItIsRemoved()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var admin = host.CreateAuthenticatedClient();
        var trackId = await TrackIdAsync(host, "madman_stadium");
        (await ImportAsync(admin, BaseGame(), ("base/env_bigstadium_large.png", TestImages.Png(2)))).Dispose();

        using var upload = await UploadAsync(admin, $"/api/catalogue/tracks/{trackId}/images/large", TestImages.Jpeg());

        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var slots = await upload.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(
            ["Import", "Upload"],
            slots.EnumerateArray().Select(s => s.GetProperty("origin").GetString()).Order());
        using (var served = await admin.GetAsync("/api/images/tracks/madman_stadium/large", Ct))
        {
            Assert.Equal("image/jpeg", served.Content.Headers.ContentType?.MediaType);
        }

        using var listed = await admin.GetAsync("/api/catalogue/images", Ct);
        Assert.Equal(2, (await listed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetArrayLength());

        using var removed = await admin.DeleteAsync($"/api/catalogue/tracks/{trackId}/images/large", Ct);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        using (var served = await admin.GetAsync("/api/images/tracks/madman_stadium/large", Ct))
        {
            Assert.Equal(TestImages.Png(2), await served.Content.ReadAsByteArrayAsync(Ct));
        }

        // The imported image is not the admin's to remove this way.
        using var again = await admin.DeleteAsync($"/api/catalogue/tracks/{trackId}/images/large", Ct);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task AVariantUpload_ShowsForThatVariantOnly()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var admin = host.CreateAuthenticatedClient();
        var variantId = await VariantIdAsync(host, "bigstadium_figure_8");

        using var upload = await UploadAsync(admin, $"/api/catalogue/variants/{variantId}/images/small", TestImages.Png(7));

        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        using var variant = await admin.GetAsync("/api/images/variants/bigstadium_figure_8/small", Ct);
        Assert.Equal(TestImages.Png(7), await variant.Content.ReadAsByteArrayAsync(Ct));
        using var arena = await admin.GetAsync("/api/images/variants/bigstadium_demolition_arena/small", Ct);
        Assert.Equal(HttpStatusCode.NotFound, arena.StatusCode);
    }

    [Fact]
    public async Task AnUploadThatIsNotAnImage_IsRefused()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var admin = host.CreateAuthenticatedClient();
        var trackId = await TrackIdAsync(host, "madman_stadium");

        using var svg = await UploadAsync(admin, $"/api/catalogue/tracks/{trackId}/images/large", "<svg onload=alert(1)/>"u8.ToArray());
        using var missing = await UploadAsync(admin, "/api/catalogue/tracks/999999/images/large", TestImages.Png(1));
        using var badSize = await UploadAsync(admin, $"/api/catalogue/tracks/{trackId}/images/loading", TestImages.Png(1));

        Assert.Equal(HttpStatusCode.BadRequest, svg.StatusCode);
        Assert.Contains("not a PNG, JPEG or WebP", await svg.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badSize.StatusCode);
    }

    [Fact]
    public async Task ImportWithoutADump_OrWithANewerOne_IsRefused()
    {
        await using var host = await ApiTestHost.StartAsync();
        using var admin = host.CreateAuthenticatedClient();

        using var noDump = new MultipartFormDataContent();
        noDump.Add(Png(TestImages.Png(1)), "images", "base/a.png");
        using var refused = await admin.PostAsync(ImportUrl, noDump, Ct);
        using var newer = await ImportAsync(admin, new TrackDumpFixture { FormatVersion = 2 }.Base());

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, newer.StatusCode);
        Assert.Contains("formatVersion 2", await newer.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> ImportAsync(HttpClient client, TrackDumpFixture dump, params (string Name, byte[] Data)[] images)
    {
        using var form = new MultipartFormDataContent();
        var json = new ByteArrayContent(Encoding.UTF8.GetBytes(dump.ToJson()));
        json.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(json, "dump", "tracks.json");
        foreach (var (name, data) in images)
        {
            form.Add(Png(data), "images", name);
        }

        return await client.PostAsync(ImportUrl, form, Ct);
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string url, byte[] data)
    {
        using var form = new MultipartFormDataContent();
        form.Add(Png(data), "file", "picture.png");
        return await client.PutAsync(url, form, Ct);
    }

    private static ByteArrayContent Png(byte[] data)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return content;
    }

    private static async Task<int> TrackIdAsync(ApiTestHost host, string key)
    {
        using var admin = host.CreateAuthenticatedClient();
        var tracks = await admin.GetFromJsonAsync<JsonElement>("/api/catalogue/tracks", Ct);
        return tracks.EnumerateArray().Single(t => t.GetProperty("key").GetString() == key).GetProperty("id").GetInt32();
    }

    private static async Task<int> VariantIdAsync(ApiTestHost host, string variantId)
    {
        using var admin = host.CreateAuthenticatedClient();
        var tracks = await admin.GetFromJsonAsync<JsonElement>("/api/catalogue/tracks", Ct);
        return tracks.EnumerateArray()
            .SelectMany(t => t.GetProperty("variants").EnumerateArray())
            .Single(v => v.GetProperty("variantId").GetString() == variantId)
            .GetProperty("id").GetInt32();
    }
}
