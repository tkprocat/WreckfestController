using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using WreckfestController.Models;
using WreckfestController.Services.Assistant;
using WreckfestController.Services.Config;
using static WreckfestController.Tests.Api.AssistantEndpointsTests;

namespace WreckfestController.Tests.Api;

/// <summary>
/// /api/assistant: the admin panel's assistant. Admins only; it reads the admin's view and
/// proposes changes, but writes nothing, and never shows the model a password or Steam id.
/// </summary>
public sealed class AdminAssistantEndpointsTests : IDisposable
{
    private const string Key = "sk-test-admin-assistant-key";
    private const string Password = "hunter2-server-password";
    private const string CupPassword = "cup-night-password";
    private const string AdminSteamId = "76561197985810610";

    private readonly string _directory = ApiTestHost.NewDataDirectory();
    private readonly FakeModel _model = new();
    private readonly string _config;

    public AdminAssistantEndpointsTests()
    {
        Directory.CreateDirectory(_directory);
        _config = Path.Combine(_directory, "server_config.cfg");
        File.WriteAllLines(_config,
        [
            "server_name=Friday Wrecks",
            $"password={Password}",
            "max_players=16",
            $"admin_steam_ids={AdminSteamId}",
            "",
            "# Event Loop",
            "el_add=bigstadium_demolition_arena",
            "el_laps=5",
        ]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string InOneDay => DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-ddTHH:mm:00Z", CultureInfo.InvariantCulture);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Anonymous_IsRefused()
    {
        await using var host = await StartAsync();
        await ConfigureAsync(host, Key);
        using var browser = host.CreateBrowser();
        await browser.FetchAntiforgeryAsync();

        using var status = await browser.Http.GetAsync("/api/assistant", Ct);
        using var chat = await AskAsync(browser.Http, "Change the rotation.");

        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, chat.StatusCode);
        Assert.Empty(_model.Requests);
    }

    [Fact]
    public async Task Status_NeedsOnlyAKey_NotThePublicPage()
    {
        await using var host = await StartAsync();
        using var client = host.CreateAuthenticatedClient();

        Assert.False(await AvailableAsync(client));

        await ConfigureAsync(host, Key, enabled: false);
        Assert.True(await AvailableAsync(client));
    }

    [Fact]
    public async Task Chat_WithoutAKey_Answers404_AndAsksNothing()
    {
        await using var host = await StartAsync();
        using var client = host.CreateAuthenticatedClient();

        using var response = await AskAsync(client, "Hello?");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_model.Requests);
    }

    [Fact]
    public async Task Chat_ProposesChanges_AndWritesNothing()
    {
        await using var host = await StartAsync();
        await ConfigureAsync(host, Key);
        using var client = host.CreateAuthenticatedClient();
        var cup = await CreateCupAsync(client);
        var before = File.ReadAllText(_config);
        var rotation = await client.GetFromJsonAsync<JsonElement>("/api/config/tracks", Ct);

        _model.Answer(ToolCalls(
            ("get_rotation", "{}"),
            ("get_server_config", "{}"),
            ("list_cups", "{}")));
        _model.Answer(ToolCalls(
            ("propose_rotation", Json(new { collectionName = "Ovals", tracks = new[] { new { track = "speedway2_figure_8", laps = 3 } }, version = rotation.GetProperty("version").GetString(), summary = "Ovals only" })),
            ("propose_server_config", """{"changes":{"maxPlayers":20,"laps":4}}"""),
            ("propose_cup", Json(new { id = cup.Id, version = int.Parse(cup.Version, CultureInfo.InvariantCulture), cup = new { name = "Race night", startTime = InOneDay, serverConfig = new { serverName = "Cup Night", laps = 6 } } })),
            ("propose_collection", """{"name":"Ovals","tracks":[{"track":"speedway2_figure_8"}]}""")));
        _model.Answer(Text("Here are the changes. Apply the ones you want."));

        using var response = await AskAsync(client, "Make it ovals only, 20 players, and rename the cup's server.");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.ETag);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Here are the changes. Apply the ones you want.", body.GetProperty("reply").GetString());

        var proposals = body.GetProperty("proposals").EnumerateArray().ToList();
        Assert.Equal(
            ["setRotation", "updateServerConfig", "updateCup", "createCollection"],
            proposals.Select(p => p.GetProperty("kind").GetString()));

        var setRotation = proposals[0];
        Assert.Equal("Ovals only", setRotation.GetProperty("summary").GetString());
        Assert.Equal(rotation.GetProperty("version").GetString(), setRotation.GetProperty("version").GetString());
        Assert.Equal("Ovals", setRotation.GetProperty("body").GetProperty("collectionName").GetString());

        Assert.Equal(20, proposals[1].GetProperty("body").GetProperty("maxPlayers").GetInt32());

        // The cup keeps the password the model never saw.
        var updateCup = proposals[2];
        Assert.Equal(cup.Id, updateCup.GetProperty("targetId").GetInt32());
        Assert.Equal(cup.Version, updateCup.GetProperty("version").GetString());
        var cupConfig = updateCup.GetProperty("body").GetProperty("serverConfig");
        Assert.Equal("Cup Night", cupConfig.GetProperty("serverName").GetString());
        Assert.Equal(CupPassword, cupConfig.GetProperty("password").GetString());

        // Nothing was written.
        Assert.Equal(before, File.ReadAllText(_config));
        Assert.Empty(await client.GetFromJsonAsync<JsonElement[]>("/api/collections", Ct) ?? []);

        // What the model saw left out the secrets.
        var toModel = string.Join('\n', _model.Requests.Select(r => r.Body.ToString()));
        Assert.DoesNotContain(Password, toModel, StringComparison.Ordinal);
        Assert.DoesNotContain(CupPassword, toModel, StringComparison.Ordinal);
        Assert.DoesNotContain(AdminSteamId, toModel, StringComparison.Ordinal);
        Assert.Contains("Friday Wrecks", toModel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProposal_AppliesThroughTheAdminApi()
    {
        await using var host = await StartAsync();
        await ConfigureAsync(host, Key);
        using var client = host.CreateAuthenticatedClient();
        var version = (await client.GetFromJsonAsync<JsonElement>("/api/config/tracks", Ct)).GetProperty("version").GetString();
        _model.Answer(ToolCalls(("get_rotation", "{}")));
        _model.Answer(ToolCalls(
            ("propose_rotation", Json(new { collectionName = "Ovals", tracks = new[] { new { track = "speedway2_figure_8", laps = 3 } }, version }))));
        _model.Answer(Text("Apply it when you are ready."));

        using var response = await AskAsync(client, "Ovals only, please.");
        var proposal = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("proposals")[0];

        using var put = new HttpRequestMessage(HttpMethod.Put, "/api/config/tracks")
        {
            Content = JsonContent.Create(proposal.GetProperty("body")),
        };
        put.Headers.TryAddWithoutValidation("If-Match", $"\"{proposal.GetProperty("version").GetString()}\"");
        using var applied = await client.SendAsync(put, Ct);

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var rotation = await client.GetFromJsonAsync<JsonElement>("/api/config/tracks", Ct);
        Assert.Equal("Ovals", rotation.GetProperty("collectionName").GetString());
        Assert.Equal("speedway2_figure_8", rotation.GetProperty("tracks")[0].GetProperty("track").GetString());
    }

    [Fact]
    public async Task AProposal_CarriesTheVersionTheModelRead_SoALaterEditIsNotOverwritten()
    {
        await using var host = await StartAsync();
        await ConfigureAsync(host, Key);
        using var client = host.CreateAuthenticatedClient();
        var read = (await client.GetFromJsonAsync<JsonElement>("/api/config/tracks", Ct)).GetProperty("version").GetString();
        _model.Answer(ToolCalls(("get_rotation", "{}")));
        // The model looks again in the same round as it proposes, with what it read before:
        // the second lookup must not lend the proposal the newer version.
        _model.Answer(ToolCalls(
            ("get_rotation", "{}"),
            ("propose_rotation", Json(new { collectionName = "Ovals", tracks = new[] { new { track = "speedway2_figure_8" } }, version = read }))));
        _model.Answer(Text("Apply it when you are ready."));

        // Someone edits the rotation after the model read it, before it proposes.
        _model.BeforeAnswer = previous =>
        {
            if (previous == 1)
            {
                File.AppendAllLines(_config, ["el_add=speedway2_figure_8"]);
            }
        };

        using var response = await AskAsync(client, "Ovals only, please.");
        var proposal = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("proposals")[0];
        Assert.Equal(read, proposal.GetProperty("version").GetString());

        using var put = new HttpRequestMessage(HttpMethod.Put, "/api/config/tracks")
        {
            Content = JsonContent.Create(proposal.GetProperty("body")),
        };
        put.Headers.TryAddWithoutValidation("If-Match", $"\"{proposal.GetProperty("version").GetString()}\"");
        using var applied = await client.SendAsync(put, Ct);

        Assert.Equal(HttpStatusCode.Conflict, applied.StatusCode);
    }

    [Fact]
    public async Task ACupUpdate_KeepsAnEmptyPassword()
    {
        await using var host = await StartAsync();
        await ConfigureAsync(host, Key);
        using var client = host.CreateAuthenticatedClient();
        var cup = await CreateCupAsync(client, password: "");
        _model.Answer(ToolCalls(("list_cups", "{}")));
        _model.Answer(ToolCalls(
            ("propose_cup", Json(new { id = cup.Id, version = cup.Version, cup = new { name = "Renamed night", startTime = InOneDay } }))));
        _model.Answer(Text("Apply it when you are ready."));

        using var response = await AskAsync(client, "Rename the cup.");

        var proposal = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("proposals")[0];
        Assert.Equal("updateCup", proposal.GetProperty("kind").GetString());
        Assert.Equal("", proposal.GetProperty("body").GetProperty("serverConfig").GetProperty("password").GetString());
    }

    [Fact]
    public async Task AnUpdate_NeedsTheVersionItIsBasedOn()
    {
        await using var host = await StartAsync();
        await ConfigureAsync(host, Key);
        using var client = host.CreateAuthenticatedClient();
        var cup = await CreateCupAsync(client);
        _model.Answer(ToolCalls(
            ("propose_cup", Json(new { id = cup.Id, cup = new { name = "Renamed night", startTime = InOneDay } }))));
        _model.Answer(Text("I could not propose that."));

        using var response = await AskAsync(client, "Rename the cup.");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Empty(body.GetProperty("proposals").EnumerateArray());
        var result = _model.Requests[1].Body.GetProperty("messages").EnumerateArray().Last();
        Assert.Contains("version is required", result.GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    public static TheoryData<string, string> RefusedProposals => new()
    {
        { "propose_server_config", """{"changes":{"password":"letmein"}}""" },
        { "propose_server_config", Json(new { changes = new { adminSteamIds = AdminSteamId } }) },
        { "propose_server_config", """{"changes":{"maxPlayers":"lots"}}""" },
        { "propose_rotation", """{"collectionName":"Ovals","tracks":[{"laps":3}]}""" },
        { "propose_rotation", """{"collectionName":"Ovals\nel_add=x","tracks":[{"track":"speedway2_figure_8"}]}""" },
        { "propose_cup", Json(new { cup = new { name = "Race night", startTime = InOneDay, serverConfig = new { password = "letmein" } } }) },
        { "propose_cup", Json(new { cup = new { name = "Race night", startTime = InOneDay, serverConfig = new { Password = "letmein" } } }) },
        { "propose_cup", Json(new { cup = new { name = "Race night", startTime = InOneDay, ServerConfig = new { PASSWORD = "letmein" } } }) },
        { "propose_rotation", """{"collectionName":"Ovals","tracks":[{"track":"speedway2_figure_8"}]}""" },
        { "propose_rotation", """{"collectionName":"Ovals","tracks":[{"track":"speedway2_figure_8"}],"version":"\"\""}""" },
        { "propose_rotation", """{"collectionName":"Ovals","tracks":[{"track":"speedway2_figure_8"}],"version":"latest"}""" },
        { "propose_collection", """{"id":999,"name":"Ovals","tracks":[{"track":"speedway2_figure_8"}]}""" },
        { "propose_deploy_collection", """{"id":999}""" },
    };

    [Theory]
    [MemberData(nameof(RefusedProposals))]
    public async Task ABadProposal_IsRefusedToTheModel_AndNotShown(string tool, string arguments)
    {
        await using var host = await StartAsync();
        await ConfigureAsync(host, Key);
        using var client = host.CreateAuthenticatedClient();
        _model.Answer(ToolCalls((tool, arguments)));
        _model.Answer(Text("I could not propose that."));

        using var response = await AskAsync(client, "Do it.");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Empty(body.GetProperty("proposals").EnumerateArray());
        var result = _model.Requests[1].Body.GetProperty("messages").EnumerateArray().Last();
        Assert.Equal("tool", result.GetProperty("role").GetString());
        Assert.Contains("\"error\"", result.GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Proposals_AreCappedPerQuestion()
    {
        await using var host = await StartAsync();
        await ConfigureAsync(host, Key);
        using var client = host.CreateAuthenticatedClient();
        _model.Answer(ToolCalls(Enumerable.Range(1, AdminAssistantTools.MaxProposals + 1)
            .Select(i => ("propose_server_config", Json(new { changes = new { maxPlayers = i } })))
            .ToArray()));
        _model.Answer(Text("Done."));

        using var response = await AskAsync(client, "Try every player count.");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(AdminAssistantTools.MaxProposals, body.GetProperty("proposals").GetArrayLength());
    }

    [Fact]
    public async Task TheAdminTools_AreOffered_WithTheAdminPrompt()
    {
        await using var host = await StartAsync();
        await ConfigureAsync(host, Key);
        using var client = host.CreateAuthenticatedClient();
        _model.Answer(Text("Hi."));

        using var response = await AskAsync(client, "Hello?");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var request = Assert.Single(_model.Requests).Body;
        var offered = request.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("function").GetProperty("name").GetString())
            .ToList();
        Assert.Equal(AdminAssistantTools.Definitions.Select(t => t.Name), offered);
        Assert.Contains("admin panel", request.GetProperty("messages")[0].GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);

    private static async Task<(int Id, string Version)> CreateCupAsync(HttpClient client, string password = CupPassword)
    {
        using var created = await client.PostAsJsonAsync(
            "/api/cups",
            new { name = "Race night", startTime = InOneDay, serverConfig = new { serverName = "Race Night", password } },
            Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return (body.GetProperty("id").GetInt32(), body.GetProperty("version").GetInt32().ToString(CultureInfo.InvariantCulture));
    }

    private Task<ApiTestHost> StartAsync() => ApiTestHost.StartAsync(
        new Dictionary<string, string?>
        {
            ["WreckfestServer:WorkingDirectory"] = _directory,
            ["WreckfestServer:ServerArguments"] = "-s server_config=server_config.cfg",
        },
        configureServices: services =>
            services.AddHttpClient(AssistantChat.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _model));

    private static async Task ConfigureAsync(ApiTestHost host, string apiKey, bool enabled = false)
    {
        var store = host.MainServices.GetRequiredService<ISettingsStore>();
        var entry = store.GetEntry<AssistantSettings>();
        entry.Value.Enabled = enabled;
        entry.Value.ApiKey = apiKey;
        var saved = await store.SaveAsync(entry.Value, entry.Version, Ct);
        Assert.Equal(SettingsSaveStatus.Saved, saved.Status);
    }

    private static async Task<bool> AvailableAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/assistant", Ct)).GetProperty("available").GetBoolean();

    private static Task<HttpResponseMessage> AskAsync(HttpClient client, string question) =>
        client.PostAsJsonAsync(
            "/api/assistant/chat",
            new { messages = new[] { new { role = "user", content = question } } },
            Ct);
}
