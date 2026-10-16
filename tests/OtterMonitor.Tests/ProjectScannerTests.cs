using OtterMonitor.Core;

internal static class ProjectScannerTests
{
    internal static async Task Run(string root, Func<string, Func<Task>, Task> test, Action<bool, string> check)
    {
        var repos = Path.Combine(root, "scan-" + Guid.NewGuid().ToString("N"), "repos");
        void Write(string relative, string text) { var path = Path.Combine(repos, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
        Write(@"shop\package.json", """{ "name": "@acme/shop", "scripts": { "build": "vite build", "predev": "x", "dev": "vite", "postinstall": "x", "lint": "eslint ." } }""");
        Write(@"shop\package-lock.json", "{}");
        Write(@"shop\node_modules\dep\package.json", """{ "name": "dep", "scripts": { "start": "node ." } }""");
        Write(@"shop\.output\server\package.json", """{ "name": "built", "scripts": { "start": "node index.mjs" } }""");
        Write(@"mono\package.json", """{ "name": "mono", "private": true, "packageManager": "pnpm@9.1.0", "scripts": { "dev": "turbo dev" } }""");
        Write(@"mono\apps\web\package.json", """{ "name": "web", "scripts": { "start": "next start", "preview": "x" } }""");
        Write(@"mono\pnpm-lock.yaml", "");
        Write(@"bunny\package.json", """{ /* comment */ "name": "bunny", "scripts": { "serve": "bun x", "weird name": "x", "test": "bun test", }, }""");
        Write(@"bunny\bun.lockb", "");
        Write(@"lib\package.json", """{ "name": "just-a-library", "main": "index.js" }""");
        Write(@"stack\docker-compose.yml", "services: {}");
        Write(@"stack\Api\Api.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");
        Write(@"stack\Api.Tests\Api.Tests.csproj", """<Project Sdk="Microsoft.NET.Sdk"></Project>""");
        Write(@"py\manage.py", "");
        Write(@"many\package.json", """{ "name": "many", "scripts": { "build": "x", "test": "x", "dev:api": "x", "lint": "x", "start:prod": "x" } }""");
        Write(@"EverlyAuctions\package.json", """{ "name": "nuxt-app", "scripts": { "dev": "nuxt dev" } }""");
        Write(@"broken\package.json", "{ not json");

        await test("Project scan reads manifests into ready quick start commands without entering dependency or build folders", () =>
        {
            var found = ProjectScanner.Scan([repos]);
            ProjectCandidate One(string name) => found.Single(candidate => candidate.Name == name);
            check(found.Select(candidate => candidate.Name).Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(["@acme/shop", "Api", "bunny", "EverlyAuctions", "many", "mono", "py", "stack", "web"], StringComparer.OrdinalIgnoreCase),
                "Unexpected projects: " + string.Join(", ", found.Select(candidate => candidate.Name)));
            var shop = One("@acme/shop");
            check(shop.ServiceIcon == "node" && shop.Recommended.Command == "npm run dev" && shop.Commands.Select(c => c.Label).SequenceEqual(["dev", "build", "lint"])
                && shop.Location == Path.Combine("repos", "shop"), "npm project scripts, order, hooks or location were wrong.");
            check(One("mono").Recommended.Command == "pnpm run dev" && One("web").Recommended.Command == "pnpm run start" && One("web").Commands[1].Command == "pnpm run preview",
                "A workspace package did not use the root lockfile or packageManager field.");
            check(One("bunny").ServiceIcon == "bun" && One("bunny").Recommended.Command == "bun run serve" && One("bunny").Commands.All(c => c.Label != "weird name"),
                "Bun, lenient JSON or unsafe script names were handled incorrectly.");
            check(One("stack").ServiceIcon == "docker" && One("stack").Recommended.Command == "docker compose up -d", "Compose project missing.");
            check(One("Api").ServiceIcon == "dotnet" && One("Api").Recommended.Command == "dotnet watch" && One("Api").Directory.EndsWith(@"stack\Api"), "A web .csproj was not offered or a test project was.");
            check(One("many").Commands.Select(c => c.Label).SequenceEqual(["dev:api", "start:prod", "build", "test", "lint"]) && One("many").Recommended.Label == "dev:api",
                "Server-like scripts were not offered before build, test and lint.");
            check(One("py").Recommended.Command == "python manage.py runserver", "Django project missing.");
            check(One("web").Matches("next") is false && One("web").Matches("pnpm run start") && One("web").Matches(@"apps\web"), "Project search fields were wrong.");
            return Task.CompletedTask;
        });

        await test("Project scan respects depth, missing roots and cancellation", () =>
        {
            check(!ProjectScanner.Scan([repos], maxDepth: 1).Any(candidate => candidate.Name is "web" or "Api"), "Depth limit was ignored.");
            check(ProjectScanner.Scan([Path.Combine(repos, "missing"), "relative"]).Count == 0, "Missing or relative roots produced results.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var stopped = false;
            try { ProjectScanner.Scan([repos], cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
            check(stopped, "A cancelled scan kept running.");
            check(ProjectScanner.DefaultRoots().All(Directory.Exists), "A default root that does not exist was offered.");
            return Task.CompletedTask;
        });
    }
}
