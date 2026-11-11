using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cerberus.Authorization;

namespace Cerberus.Tests;

public class PermissionPolicyTests
{
    private static PermissionPolicy Policy(PermissionMode mode, params string[] allowed) => new(mode, allowed.ToHashSet());

    [Theory]
    [InlineData(ActionKinds.ReadFiles, false)]
    [InlineData(ActionKinds.RunJob, false)]
    [InlineData(ActionKinds.EditFiles, true)]
    [InlineData(ActionKinds.RunCommands, true)]
    [InlineData(ActionKinds.SendCodeToCloudModel, true)]
    public void By_default_passive_work_runs_and_anything_that_changes_or_leaves_the_machine_asks(string kind, bool asks)
    {
        Assert.Equal(asks, PermissionPolicy.Standard.NeedsApproval(kind));
    }

    [Fact]
    public void In_ask_mode_everything_asks()
    {
        var policy = Policy(PermissionMode.Ask);

        Assert.All(ActionKinds.All, k => Assert.True(policy.NeedsApproval(k.Kind), k.Kind));
    }

    [Theory]
    [InlineData(PermissionMode.Default)]
    [InlineData(PermissionMode.Ask)]
    public void Always_allow_wins_for_that_kind_only_in_either_mode(PermissionMode mode)
    {
        var policy = Policy(mode, ActionKinds.RunCommands);

        Assert.False(policy.NeedsApproval(ActionKinds.RunCommands));
        Assert.True(policy.NeedsApproval(ActionKinds.EditFiles));
        Assert.Equal([ActionKinds.EditFiles], policy.Unapproved([ActionKinds.RunCommands, ActionKinds.EditFiles]));
    }

    [Fact]
    public void Kinds_are_a_closed_set()
    {
        Assert.True(ActionKinds.IsValid("edit-files"));
        Assert.False(ActionKinds.IsValid("delete-everything"));
        Assert.False(ActionKinds.IsValid(null));
    }
}

[Collection("api")]
public class PermissionApiTests(ApiHost host)
{
    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static string[] Allowed(JsonElement permissions) =>
        permissions.GetProperty("allowedKinds").EnumerateArray().Select(k => k.GetString()!).ToArray();

    private static async Task<JsonElement> WaitForEndAsync(HttpClient client, string jobId)
    {
        for (var i = 0; i < 400; i++)
        {
            var job = await client.GetFromJsonAsync<JsonElement>($"/jobs/{jobId}");
            if (job.GetProperty("state").GetString() is "Succeeded" or "Failed" or "Cancelled" or "TimedOut") return job;
            await Task.Delay(40);
        }
        throw new TimeoutException("job did not finish");
    }

    [Fact]
    public async Task A_new_user_has_the_standard_policy_and_can_see_what_asks()
    {
        var (client, _) = await host.NewUserAsync();

        var permissions = await client.GetFromJsonAsync<JsonElement>("/settings/permissions");

        Assert.Equal("Default", permissions.GetProperty("mode").GetString());
        Assert.Equal("standard", permissions.GetProperty("scope").GetString());
        Assert.Empty(Allowed(permissions));
        var asks = permissions.GetProperty("kinds").EnumerateArray()
            .ToDictionary(k => k.GetProperty("kind").GetString()!, k => k.GetProperty("asks").GetBoolean());
        Assert.False(asks["run-job"]);
        Assert.True(asks["run-commands"]);
        Assert.Equal(5, asks.Count);
    }

    [Fact]
    public async Task In_ask_mode_even_a_passive_scan_waits_and_always_allow_stops_the_asking()
    {
        var (client, _) = await host.NewUserAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/settings/permissions", new { mode = "ask" })).StatusCode);

        // 1. The job is parked with a request that names the action.
        var submitted = await Json(await client.PostAsJsonAsync("/jobs", new { type = "scan", path = "app", sca = false }));
        var job = submitted.GetProperty("job");
        var approval = submitted.GetProperty("approval");
        Assert.Equal("AwaitingApproval", job.GetProperty("state").GetString());
        Assert.Equal("run-job", approval.GetProperty("kind").GetString());
        Assert.Equal("Run a scan of \"app\"", approval.GetProperty("title").GetString());

        // 2. Approve, and remember: the job runs and the kind is now always allowed.
        var decided = await client.PostAsJsonAsync($"/approvals/{approval.GetProperty("id").GetString()}/approve", new { remember = true });
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
        Assert.Equal("Succeeded", (await WaitForEndAsync(client, job.GetProperty("id").GetString()!)).GetProperty("state").GetString());

        var permissions = await client.GetFromJsonAsync<JsonElement>("/settings/permissions");
        Assert.Equal("Ask", permissions.GetProperty("mode").GetString());
        Assert.Equal(["run-job"], Allowed(permissions));

        // 3. The next job is not asked about.
        var next = await Json(await client.PostAsJsonAsync("/jobs", new { type = "scan", path = "app", sca = false }));
        Assert.Equal("Queued", next.GetProperty("state").GetString());
        await WaitForEndAsync(client, next.GetProperty("id").GetString()!);

        var actions = (await client.GetFromJsonAsync<JsonElement>("/audit?limit=50")).EnumerateArray()
            .Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Equal(2, actions.Count(a => a == "permissions.update"));   // the mode change, then the "always allow"
    }

    [Fact]
    public async Task Always_allowing_cloud_models_skips_that_approval_and_says_so_in_the_audit_log()
    {
        var (client, _) = await host.NewUserAsync();
        var scan = await Json(await client.PostAsJsonAsync("/scan", new { path = "svc", sca = false }));
        var scanId = scan.GetProperty("id").GetString();
        await client.PutAsJsonAsync("/settings/permissions", new { allowedKinds = new[] { "send-code-to-cloud-model" } });
        var sentBefore = host.Cloud.Requests.Count;

        var submitted = await Json(await client.PostAsJsonAsync("/jobs", new { type = "triage", scanId, model = "claude-haiku-4-5" }));

        Assert.Equal("Queued", submitted.GetProperty("state").GetString());   // not parked
        await WaitForEndAsync(client, submitted.GetProperty("id").GetString()!);
        Assert.Equal(sentBefore + 1, host.Cloud.Requests.Count);

        var auto = (await client.GetFromJsonAsync<JsonElement>("/audit?limit=50")).EnumerateArray()
            .Single(e => e.GetProperty("action").GetString() == "approval.auto");
        Assert.Contains("always allowed by the user permissions", auto.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task In_ask_mode_a_cloud_triage_asks_once_for_both_things_it_does()
    {
        var (client, _) = await host.NewUserAsync();
        var scan = await Json(await client.PostAsJsonAsync("/scan", new { path = "svc", sca = false }));
        await client.PutAsJsonAsync("/settings/permissions", new { mode = "ask" });

        var submitted = await Json(await client.PostAsJsonAsync("/jobs",
            new { type = "triage", scanId = scan.GetProperty("id").GetString(), model = "claude-haiku-4-5" }));

        var approval = submitted.GetProperty("approval");
        Assert.Equal("send-code-to-cloud-model", approval.GetProperty("kind").GetString());   // the more sensitive one leads
        Assert.Equal(["run-job", "send-code-to-cloud-model"],
            approval.GetProperty("kinds").EnumerateArray().Select(k => k.GetString()!).ToArray());
        await client.PostAsync($"/approvals/{approval.GetProperty("id").GetString()}/deny", null);
    }

    [Fact]
    public async Task An_engagement_can_be_stricter_or_looser_than_the_user_and_remembering_stays_within_it()
    {
        var (client, _) = await host.NewUserAsync();
        var strict = (await Json(await client.PostAsJsonAsync("/engagements", new { name = "Strict" }))).GetProperty("id").GetString();
        await client.PostAsJsonAsync($"/engagements/{strict}/targets", new { locator = "app", ownershipAttested = true });
        await client.PutAsJsonAsync("/settings/permissions", new { mode = "ask", engagementId = strict });

        // The user's own policy is untouched: a job outside the engagement just runs.
        var free = await Json(await client.PostAsJsonAsync("/jobs", new { type = "scan", path = "app", sca = false }));
        Assert.Equal("Queued", free.GetProperty("state").GetString());
        await WaitForEndAsync(client, free.GetProperty("id").GetString()!);

        // Under the engagement it waits; "always allow" is remembered for that engagement only.
        var submitted = await Json(await client.PostAsJsonAsync("/jobs", new { type = "scan", path = "app", sca = false, engagementId = strict }));
        Assert.Equal("AwaitingApproval", submitted.GetProperty("job").GetProperty("state").GetString());
        await client.PostAsJsonAsync($"/approvals/{submitted.GetProperty("approval").GetProperty("id").GetString()}/approve", new { remember = true });
        await WaitForEndAsync(client, submitted.GetProperty("job").GetProperty("id").GetString()!);

        var forEngagement = await client.GetFromJsonAsync<JsonElement>($"/settings/permissions?engagementId={strict}");
        Assert.Equal(("engagement", "Ask"), (forEngagement.GetProperty("scope").GetString(), forEngagement.GetProperty("mode").GetString()));
        Assert.Equal(["run-job"], Allowed(forEngagement));
        Assert.Empty(Allowed(await client.GetFromJsonAsync<JsonElement>("/settings/permissions")));

        // Removing the override falls back to the user's policy.
        var reset = await Json(await client.DeleteAsync($"/settings/permissions?engagementId={strict}"));
        Assert.Equal("standard", reset.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task Settings_are_validated_and_private()
    {
        var (client, _) = await host.NewUserAsync();
        var (other, _) = await host.NewUserAsync();
        var engagement = (await Json(await client.PostAsJsonAsync("/engagements", new { name = "Mine" }))).GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/settings/permissions", new { mode = "yolo" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/settings/permissions", new { allowedKinds = new[] { "delete-everything" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/settings/permissions", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync("/settings/permissions", new { mode = "ask", engagementId = engagement })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/settings/permissions?engagementId={engagement}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/settings/permissions")).StatusCode);

        // One user's settings do not leak into another's.
        await client.PutAsJsonAsync("/settings/permissions", new { mode = "ask", allowedKinds = new[] { "edit-files" } });
        Assert.Equal("Default", (await other.GetFromJsonAsync<JsonElement>("/settings/permissions")).GetProperty("mode").GetString());
    }
}
