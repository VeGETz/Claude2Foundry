# 10 — Build, layout, and deploy

## Solution layout

```
/
├── Claude2Foundry.sln
├── src/
│   └── Claude2Foundry/
│       ├── Claude2Foundry.csproj
│       ├── Program.cs
│       ├── appsettings.json
│       ├── Protocol/
│       │   ├── AnthropicMessages.cs       # records per [01]
│       │   ├── OpenAIChat.cs              # records per [01]
│       │   └── SerializerContext.cs       # JsonSerializerContext source-gen
│       ├── Translation/
│       │   ├── RequestTranslator.cs       # per [02]
│       │   ├── ResponseTranslator.cs      # per [03]
│       │   ├── StreamTranslator.cs        # per [04]
│       │   └── StopReasonMap.cs
│       ├── Backend/
│       │   └── FoundryClient.cs           # HttpClient wrapper
│       ├── Config/
│       │   ├── ProxyConfig.cs             # records per [06]
│       │   └── ConfigValidation.cs        # startup validation per [06]
│       ├── Tokens/
│       │   ├── TokenCounter.cs            # per [08]
│       │   └── TokenizerRegistry.cs       # cache
│       ├── Errors/
│       │   └── ErrorMapping.cs            # per [05]
│       └── Logging/
│           └── CorrelationIdMiddleware.cs # per [09]
├── tests/
│   └── Claude2Foundry.Tests/
│       ├── Claude2Foundry.Tests.csproj
│       ├── Translation/
│       │   ├── RequestTranslatorTests.cs
│       │   ├── ResponseTranslatorTests.cs
│       │   └── StreamTranslatorTests.cs
│       ├── Tokens/
│       │   └── TokenCounterTests.cs
│       ├── Errors/
│       │   └── ErrorMappingTests.cs
│       └── Fixtures/                       # captured Anthropic/OpenAI payloads
├── docs/
│   ├── adr/
│   └── specs/                              # this directory
├── CONTEXT.md
├── README.md
└── .gitignore
```

## TargetFramework

`net10.0`. Minimum SDK: .NET 10 RTM. Nullable enabled. Implicit usings enabled.

## NuGet refs

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.AspNetCore.App" />     <!-- framework reference; implicit on web template -->
  <PackageReference Include="Microsoft.Extensions.Configuration.Json" />
  <PackageReference Include="Microsoft.Extensions.Hosting" />
  <PackageReference Include="Microsoft.ML.Tokenizers" Version="<latest>" />
</ItemGroup>
```

No `Azure.AI.OpenAI`. We talk to Foundry over raw HTTP with `HttpClient` — adding the Azure SDK pulls in `Azure.Identity` and a large dep graph for no benefit.

No JSON serializer NuGet — `System.Text.Json` ships with the runtime. Use source-gen via `JsonSerializerContext` (see [01](./01-protocol-schemas.md)).

## Tests

`xunit` + `Microsoft.NET.Test.Sdk` + `xunit.runner.visualstudio`. Use `WebApplicationFactory<Program>` from `Microsoft.AspNetCore.Mvc.Testing` for end-to-end pipeline tests with a fake Foundry. Drive translators directly with captured JSON fixtures for unit tests.

Recommended fixture pattern: capture real Anthropic request bodies from `claude` running against a logged proxy, and capture real Foundry responses with `curl` against the deployment. Stash both under `tests/Claude2Foundry.Tests/Fixtures/` and assert round-trips.

## Build commands

```
dotnet restore
dotnet build -c Release
dotnet test
```

## Publish profile

Three RIDs as v1 deliverables:

```
dotnet publish src/Claude2Foundry -c Release -r win-x64   --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
dotnet publish src/Claude2Foundry -c Release -r linux-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
dotnet publish src/Claude2Foundry -c Release -r osx-arm64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
```

Output: one executable + `appsettings.json` + `tokenizers/` (if any `HuggingFace` tokenizer is configured).

NativeAOT (`/p:PublishAot=true`) is a stretch goal. It will work only if every `JsonSerializerContext` is in place and there is no runtime reflection. Defer to v2 if it costs more than half a day.

## README contents (for end users)

Mandatory sections:

1. What it is, one paragraph.
2. Prerequisites: Azure AI Foundry resource, API key, .NET 10 runtime (or use self-contained binary).
3. Quick start:
   ```
   set FOUNDRY_API_KEY=<key>
   .\Claude2Foundry.exe
   ```
4. Configure Claude Code:
   ```
   set ANTHROPIC_BASE_URL=http://localhost:8787
   set ANTHROPIC_API_KEY=dummy
   claude
   ```
5. `appsettings.json` example, with the four config blocks (aliases, reasoning, tokenizers, timeouts) explained briefly.
6. Known limitations (from CONTEXT.md):
   - Approximate token counts.
   - Image-in-tool-result message reordering.
   - Some Foundry targets reject `tool_choice: "required"`.
   - No Foundry Local, no AAD, no TLS in v1.
7. Troubleshooting:
   - 401 → check env var name in `ApiKeyEnv` matches what's set.
   - 404 → model alias points to a Foundry model name that isn't deployed.
   - Empty thinking blocks → check `ReasoningPolicies` is set for the target.
   - Stream hangs → check `StreamIdleSeconds` and Foundry deployment regional latency.

No marketing copy. No "why we built this." Engineers read these for facts.

## CI

Out of scope for v1 implementation. Single-developer tool. If the engineer wants a build check, GitHub Actions with `dotnet test` on push is the minimum useful thing.
