using System.Text.Json;
using Umbraco.AI.Agent.Core.Agents;
using Umbraco.AI.Core.Connections;
using Umbraco.AI.Core.Contexts;
using Umbraco.AI.Core.Guardrails;
using Umbraco.AI.Core.Models;
using Umbraco.AI.Core.Profiles;
using Umbraco.AI.Core.Settings;
using Umbraco.AI.Core.Tools.Scopes;
using Umbraco.AI.OpenAI;
using Umbraco.AI.Prompt.Core.Prompts;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;

namespace Umbraco.AI.Testsite;

public sealed class AiSeedComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, ContentBootstrapHandler>();
        builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, AiSeedDataHandler>();
    }
}

/// <summary>
/// Seeds Umbraco.AI with test site configuration on first startup.
/// Idempotent: exits immediately if the connection already exists.
/// API key is read from user secrets — set it with:
///   dotnet user-secrets set "LimboAiTestsite:Gemini:ApiKey" "YOUR-KEY"
/// </summary>
public sealed class AiSeedDataHandler(
    IAIConnectionService connectionService,
    IAIProfileService profileService,
    IAIContextService contextService,
    IAIGuardrailService guardrailService,
    IAISettingsService settingsService,
    IAIPromptService promptService,
    IAIAgentService agentService,
    AIToolScopeCollection toolScopes,
    IRuntimeState runtimeState,
    ILogger<AiSeedDataHandler> logger)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    private const string ConnectionAlias = "gemini-testsite";

    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken ct)
    {
        // Skip if the Umbraco installer hasn't run yet — the database isn't ready.
        // The seed will run automatically on the next startup after install completes.
        if (runtimeState.Level != RuntimeLevel.Run)
        {
            logger.LogDebug("[AI Seed] Runtime level is {Level} — skipping seed until install is complete.", runtimeState.Level);
            return;
        }

        try
        {
            var existing = await connectionService.GetConnectionByAliasAsync(ConnectionAlias, ct);
            if (existing is not null)
            {
                logger.LogDebug("[AI Seed] Already seeded — skipping.");
                return;
            }

            logger.LogInformation("[AI Seed] First startup — seeding AI configuration…");
            await SeedAsync(ct);
            logger.LogInformation("[AI Seed] Seed completed.");
        }
        catch (Exception ex)
        {
            // EF Core contexts may not be fully initialised during the installer's
            // in-process restart. Log and move on — the seed will run on the next
            // full process restart once the database is ready.
            logger.LogWarning(ex, "[AI Seed] Seed skipped (database not ready yet): {Message}", ex.Message);
        }
    }

    private async Task SeedAsync(CancellationToken ct)
    {
        // ── Context ───────────────────────────────────────────────────────────
        var context = await contextService.SaveContextAsync(new AIContext
        {
            Alias = "testsite-context",
            Name = "Test Site — Editorial Style",
            Resources =
            [
                new AIContextResource
                {
                    ResourceTypeId = "brand-voice",
                    Name = "Editorial Guidelines",
                    Description = "Test site tone of voice and writing guidelines",
                    SortOrder = 0,
                    Settings = new
                    {
                        ToneDescription = "Clear, concise and professional. Write in plain English.",
                        TargetAudience = "General audience. Avoid jargon.",
                        StyleGuidelines = "Use active voice. Short sentences. Address the reader as 'you'."
                    },
                    InjectionMode = AIContextResourceInjectionMode.Always
                }
            ]
        }, ct);

        // ── Connection ────────────────────────────────────────────────────────
        // $ prefix: Umbraco.AI resolves the value from IConfiguration at runtime.
        // Set the key with: dotnet user-secrets set "LimboAiTestsite:Gemini:ApiKey" "YOUR-KEY"
        var connection = await connectionService.SaveConnectionAsync(new AIConnection
        {
            Alias = ConnectionAlias,
            Name = "Google Gemini (Test)",
            ProviderId = "openai",
            Settings = new OpenAIProviderSettings
            {
                ApiKey = "$LimboAiTestsite:Gemini:ApiKey",
                Endpoint = "https://generativelanguage.googleapis.com/v1beta/openai/",
            },
            IsActive = true
        }, ct);

        // ── Guardrail ─────────────────────────────────────────────────────────
        var guardrail = await guardrailService.SaveGuardrailAsync(new AIGuardrail
        {
            Alias = "quality-check",
            Name = "Quality Check",
            Rules =
            [
                new AIGuardrailRule
                {
                    EvaluatorId = "llm-judge",
                    Name = "Check tone and clarity",
                    Phase = AIGuardrailPhase.PostGenerate,
                    Action = AIGuardrailAction.Warn,
                    SortOrder = 0,
                    Config = ToJsonElement(new
                    {
                        evaluationCriteria = "Check if the text is clear, professional and avoids jargon.",
                        safetyThreshold = 0.75
                    })
                }
            ]
        }, ct);

        // ── Profile ───────────────────────────────────────────────────────────
        var profile = await profileService.SaveProfileAsync(new AIProfile
        {
            Alias = "testsite-chat",
            Name = "Test Site Chat",
            Capability = AICapability.Chat,
            ConnectionId = connection.Id,
            Model = new AIModelRef("openai", "gemini-2.5-flash"),
            Settings = new AIChatProfileSettings
            {
                Temperature = 0.4f,
                ContextIds = [context.Id],
                GuardrailIds = [guardrail.Id]
            }
        }, ct);

        var aiSettings = await settingsService.GetSettingsAsync(ct);
        aiSettings.DefaultChatProfileId = profile.Id;
        await settingsService.SaveSettingsAsync(aiSettings, ct);

        // ── Prompts ───────────────────────────────────────────────────────────
        var allTextEditors = new[]
        {
            "Umb.PropertyEditorUi.TextArea",
            "Umb.PropertyEditorUi.TextBox",
            "Umb.PropertyEditorUi.Tiptap",
        };

        await promptService.SavePromptAsync(new AIPrompt
        {
            Alias = "rewrite",
            Name = "Rewrite",
            Description = "Rewrite the selected text for clarity and tone",
            Instructions =
                "Rewrite the following text to be clear, concise and professional:\n\n" +
                "{{currentValue}}\n\nReturn only the rewritten text.",
            ProfileId = profile.Id,
            IsActive = true,
            IncludeEntityContext = false,
            Scope = new AIPromptScope()
        }, ct);

        await promptService.SavePromptAsync(new AIPrompt
        {
            Alias = "summarise",
            Name = "Summarise",
            Description = "Summarise the selected text in one short paragraph",
            Instructions =
                "Summarise the following text in one short, clear paragraph:\n\n" +
                "{{currentValue}}\n\nReturn only the summary.",
            ProfileId = profile.Id,
            IsActive = true,
            IncludeEntityContext = false,
            Scope = new AIPromptScope()
        }, ct);

        // ── Agent ─────────────────────────────────────────────────────────────
        var allToolScopeIds = toolScopes.Select(x => x.Id).ToArray();

        await agentService.SaveAgentAsync(new AIAgent
        {
            Alias = "content-assistant",
            Name = "Content Assistant",
            Description = "Helps write and edit content on this site",
            ProfileId = profile.Id,
            SurfaceIds = ["copilot"],
            Scope = new AIAgentScope
            {
                AllowRules = [new AIAgentScopeRule { Sections = ["content"] }]
            },
            Config = new AIStandardAgentConfig
            {
                ContextIds = [context.Id],
                Instructions =
                    "You are a content assistant for this Umbraco test site. " +
                    "Help editors write and edit content that is clear, professional and well-structured. " +
                    "When referring to a content node, always provide a direct backoffice link using the " +
                    "Umbraco 17 format: https://localhost:44356/umbraco/section/content/workspace/document/edit/{nodeKey}/en-US/ " +
                    "Use the nodeKey (GUID) returned by get_umbraco_content. " +
                    "Do NOT use the old #/content/content/edit/ format.",
                AllowedToolScopeIds = allToolScopeIds
            },
            IsActive = true
        }, ct);
    }

    private static JsonElement ToJsonElement(object value)
    {
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }
}
