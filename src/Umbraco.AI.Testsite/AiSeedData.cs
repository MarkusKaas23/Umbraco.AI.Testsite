using System.Text.Json;
using Limbo.Umbraco.AI.Extensions;
using Limbo.Umbraco.AI.Providers.Google;
using Umbraco.AI.Agent.Core.Agents;
using Umbraco.AI.Core.Connections;
using Umbraco.AI.Core.Contexts;
using Umbraco.AI.Core.Models;
using Umbraco.AI.Core.Profiles;
using Umbraco.AI.Core.Settings;
using Umbraco.AI.Core.Tools.Scopes;
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
        builder.AddLimboAiDefaultContexts();

        // Opt in to the Limbo AI translation feature (from Limbo.Umbraco.AI package).
        // Reuses the same Gemini key already set in user secrets for the AI connection.
        // Set the key with: dotnet user-secrets set "LimboAiTestsite:Gemini:ApiKey" "YOUR-KEY"
        builder.AddLimboAiTranslation(o => o.ApiKeyConfigPath = "LimboAiTestsite:Gemini:ApiKey");

        // Opt in to the Limbo AI document import feature (from Limbo.Umbraco.AI package).
        // Reuses the same Gemini API key — no separate configuration needed.
        builder.AddLimboAiDocumentImport(o => o.ApiKeyConfigPath = "LimboAiTestsite:Gemini:ApiKey");

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
                logger.LogDebug("[AI Seed] Already seeded — checking for new items…");
                await EnsureNewItemsAsync(ct);
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
            ProviderId = "google",
            Settings = new GoogleProviderSettings
            {
                // $ prefix: Umbraco.AI resolves the value from IConfiguration at runtime.
                // Set the key with: dotnet user-secrets set "LimboAiTestsite:Gemini:ApiKey" "YOUR-KEY"
                ApiKey = "$LimboAiTestsite:Gemini:ApiKey",
                Endpoint = "https://generativelanguage.googleapis.com/v1beta/openai/",
            },
            IsActive = true
        }, ct);

        // ── Profile ───────────────────────────────────────────────────────────
        // Note: no guardrail — llm-judge sends a secondary LLM call whose request
        // format is rejected by Gemini's OpenAI-compat layer (HTTP 400).
        var profile = await profileService.SaveProfileAsync(new AIProfile
        {
            Alias = "testsite-chat",
            Name = "Test Site Chat",
            Capability = AICapability.Chat,
            ConnectionId = connection.Id,
            Model = new AIModelRef("google", "gemini-2.5-flash"),
            Settings = new AIChatProfileSettings
            {
                Temperature = 0.4f,
                ContextIds = [context.Id],
            }
        }, ct);

        var aiSettings = await settingsService.GetSettingsAsync(ct);
        aiSettings.DefaultChatProfileId = profile.Id;
        await settingsService.SaveSettingsAsync(aiSettings, ct);

        // ── Scope sets ────────────────────────────────────────────────────────
        // Short text fields: titles, SEO fields, one-liners
        var shortTextEditors = new[]
        {
            "Umb.PropertyEditorUi.TextArea",
            "Umb.PropertyEditorUi.TextBox",
        };

        // All text editors including rich text body
        var allTextEditors = new[]
        {
            "Umb.PropertyEditorUi.TextArea",
            "Umb.PropertyEditorUi.TextBox",
            "Umb.PropertyEditorUi.Tiptap",
        };

        // ── Rewrite ───────────────────────────────────────────────────────────
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
            Scope = new AIPromptScope
            {
                AllowRules = [new AIPromptScopeRule { PropertyEditorUiAliases = allTextEditors }]
            }
        }, ct);

        // ── Summarise ─────────────────────────────────────────────────────────
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
            Scope = new AIPromptScope
            {
                AllowRules = [new AIPromptScopeRule { PropertyEditorUiAliases = allTextEditors }]
            }
        }, ct);

        // ── Alt text ──────────────────────────────────────────────────────────
        await promptService.SavePromptAsync(new AIPrompt
        {
            Alias = "generate-alt-text",
            Name = "Generate alt text",
            Description = "Generates descriptive alt text for the associated image",
            Instructions =
                "Describe the image for screen readers. Be concise and accurate. " +
                "Do not start with 'Image of' or 'Photo of'. " +
                "Return only the alt text — no explanation or extra text.",
            ProfileId = profile.Id,
            IsActive = true,
            IncludeEntityContext = true,
            Scope = new AIPromptScope
            {
                // Two rules: Umbraco's built-in Image media type (altText)
                // and the testArticle content type (imageAlt).
                AllowRules =
                [
                    new AIPromptScopeRule
                    {
                        ContentTypeAliases      = ["Image"],
                        PropertyAliases         = ["altText"],
                        PropertyEditorUiAliases = ["Umb.PropertyEditorUi.TextBox"]
                    },
                    new AIPromptScopeRule
                    {
                        ContentTypeAliases      = ["testArticle"],
                        PropertyAliases         = ["imageAlt"],
                        PropertyEditorUiAliases = ["Umb.PropertyEditorUi.TextBox"]
                    }
                ]
            }
        }, ct);

        // ── SEO: meta title ───────────────────────────────────────────────────
        // Reads the full entity context so the AI knows the page topic.
        await promptService.SavePromptAsync(new AIPrompt
        {
            Alias = "seo-meta-title",
            Name = "SEO: Meta title",
            Description = "Generate an SEO-optimised page title (max 60 characters)",
            Instructions =
                "Based on the page content, write an SEO-optimised page title. " +
                "Maximum 60 characters. Include the primary keyword naturally. " +
                "Start with the main topic. Do not append the site name. " +
                "Return only the title.",
            ProfileId = profile.Id,
            IsActive = true,
            IncludeEntityContext = true,
            OptionCount = 3,
            Scope = new AIPromptScope
            {
                AllowRules = [new AIPromptScopeRule { PropertyEditorUiAliases = shortTextEditors }]
            }
        }, ct);

        // ── SEO: meta description ─────────────────────────────────────────────
        await promptService.SavePromptAsync(new AIPrompt
        {
            Alias = "seo-meta-description",
            Name = "SEO: Meta description",
            Description = "Generate an SEO-optimised meta description (150–160 characters)",
            Instructions =
                "Based on the page content, write an SEO-optimised meta description. " +
                "150–160 characters. Include relevant keywords naturally. " +
                "Invite the reader to click. Write in clear, accessible language. " +
                "Return only the description.",
            ProfileId = profile.Id,
            IsActive = true,
            IncludeEntityContext = true,
            OptionCount = 3,
            Scope = new AIPromptScope
            {
                AllowRules = [new AIPromptScopeRule { PropertyEditorUiAliases = shortTextEditors }]
            }
        }, ct);

        // ── GEO: Schema.org JSON-LD ───────────────────────────────────────────
        // Generates structured data for search engines and AI crawlers.
        // Scope: TextArea — editors paste the output into a dedicated JSON-LD field.
        // NOTE: The explicit "do not call tools" line is required because Gemini's
        // OpenAI-compat endpoint sometimes calls list_context_resources without the
        // required 'args' parameter, crashing the invocation pipeline. Telling the
        // model the context is already available prevents this redundant tool call.
        await promptService.SavePromptAsync(new AIPrompt
        {
            Alias = "schema-org-jsonld",
            Name = "Schema.org JSON-LD",
            Description = "Generate Schema.org structured data for this page",
            Instructions =
                "You already have all the context you need in the entity information provided. " +
                "Do not call any additional tools or fetch any external resources.\n\n" +
                "Based on the entity context, generate appropriate Schema.org JSON-LD structured data. " +
                "Select the most relevant Schema.org type for this content " +
                "(e.g. WebPage, Article, FAQPage, Organization, BreadcrumbList). " +
                "Populate all properties that can be inferred from the entity context. " +
                "Return only the JSON-LD object — no surrounding <script> tags, no explanation. " +
                "The output must be valid JSON.",
            ProfileId = profile.Id,
            IsActive = true,
            IncludeEntityContext = true,
            OptionCount = 1,
            Scope = new AIPromptScope
            {
                AllowRules =
                [
                    new AIPromptScopeRule
                    {
                        PropertyEditorUiAliases = ["Umb.PropertyEditorUi.TextArea"]
                    }
                ]
            }
        }, ct);

        // NOTE — Tone of voice:
        // Tone checking is intentionally NOT implemented as a prompt button.
        // It belongs as a post-generate guardrail (llm-judge evaluator) that warns
        // editors when AI output drifts outside editorial guidelines.
        // Guardrails using llm-judge are currently incompatible with Gemini's
        // OpenAI-compat endpoint (HTTP 400). Enable once a native Gemini API
        // provider or OpenAI connection is available.

        // NOTE — Per-property translation:
        // Single-field translate prompts are removed. Full-page translation is
        // handled by the translation-assistant Copilot agent (see below), which
        // reads all text fields at once and presents them translated, ready for
        // the editor to apply as an unpublished copy or culture variant.

        // ── Document → content ────────────────────────────────────────────────
        // Editor pastes raw document text into a TextArea or rich-text field,
        // triggers this prompt. IncludeEntityContext = true so the AI knows
        // the purpose of the current field from the document type.
        await promptService.SavePromptAsync(new AIPrompt
        {
            Alias = "structure-document",
            Name = "Structure document content",
            Description = "Restructure pasted document text to fit this field",
            Instructions =
                "The current field contains raw text copied from an external document " +
                "(e.g. a Word file, PDF or email). " +
                "Using the entity context to understand the purpose of this field, " +
                "rewrite and restructure the pasted text so it reads as polished, " +
                "publication-ready content appropriate for this field. " +
                "Preserve all factual information. Remove document artefacts " +
                "(headers, page numbers, formatting codes). " +
                "Return only the restructured text.\n\n" +
                "Pasted content:\n{{currentValue}}",
            ProfileId = profile.Id,
            IsActive = true,
            IncludeEntityContext = true,
            OptionCount = 1,
            Scope = new AIPromptScope
            {
                AllowRules =
                [
                    new AIPromptScopeRule
                    {
                        PropertyEditorUiAliases =
                        [
                            "Umb.PropertyEditorUi.TextArea",
                            "Umb.PropertyEditorUi.Tiptap",
                        ]
                    }
                ]
            }
        }, ct);

        // ── Agents ────────────────────────────────────────────────────────────
        var allToolScopeIds = toolScopes.Select(x => x.Id).ToArray();

        // Content assistant — general writing help in the Content section
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
                    "Umbraco 17 format: https://localhost:44392/umbraco/section/content/workspace/document/edit/{nodeKey}/en-US/ " +
                    "Use the nodeKey (GUID) returned by get_umbraco_content. " +
                    "Do NOT use the old #/content/content/edit/ format.",
                AllowedToolScopeIds = allToolScopeIds
            },
            IsActive = true
        }, ct);

        // Media assistant — alt text and captions in the Media section
        await agentService.SaveAgentAsync(new AIAgent
        {
            Alias = "media-assistant",
            Name = "Media Assistant",
            Description = "Helps write alt text and captions for images and media",
            ProfileId = profile.Id,
            SurfaceIds = ["copilot"],
            Scope = new AIAgentScope
            {
                AllowRules = [new AIAgentScopeRule { Sections = ["media"] }]
            },
            Config = new AIStandardAgentConfig
            {
                ContextIds = [context.Id],
                Instructions =
                    "You are a media assistant for this Umbraco test site. " +
                    "Help editors write accurate, descriptive alt text for images and other media. " +
                    "Focus on accessibility: describe visual content precisely for " +
                    "people using screen readers. Keep alt text concise and meaningful. " +
                    "Do not start descriptions with 'Image of' or 'Photo of'.",
                AllowedToolScopeIds = allToolScopeIds
            },
            IsActive = true
        }, ct);

        // Migration assistant — analyses existing Umbraco content and produces a
        // precise, actionable migration plan the editor can follow or apply.
        // VALUE: saves editors from manually opening two nodes side-by-side and
        // figuring out which field maps where — especially useful when document
        // types have different field names or structures.
        // CONSTRAINT: image pickers and block editors cannot be written
        // automatically (Gemini compat rejects complex setter schemas);
        // those fields are flagged for manual handling.
        await agentService.SaveAgentAsync(new AIAgent
        {
            Alias = "migration-assistant",
            Name = "Migration Assistant",
            Description = "Reads two content nodes and produces a field-by-field migration plan",
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
                    "You are a content migration analyst for this Umbraco site.\n\n" +
                    "Your job: read a SOURCE content node and a TARGET content node (or document type), " +
                    "then produce a precise migration plan the editor can act on immediately.\n\n" +
                    "STEP 1 — Identify source and target.\n" +
                    "The currently open page (entity context) is the source by default. " +
                    "Ask the editor for the target node name, URL or key if they haven't given it. " +
                    "Use get_umbraco_content to read both nodes. " +
                    "Use get_content_type_schema to understand the target document type structure.\n\n" +
                    "STEP 2 — Produce a migration report with these three sections:\n\n" +
                    "## Field mapping\n" +
                    "A table: Source field | Current value (truncated) | Target field | Action\n" +
                    "Action is one of: Copy as-is / Rewrite / Trim to N chars / Manual (too complex)\n\n" +
                    "## Content that needs rewriting\n" +
                    "Source content that doesn't fit the target field directly — show the editor what " +
                    "needs adapting and suggest how.\n\n" +
                    "## Gaps in the target\n" +
                    "Target fields that have no matching source content — the editor needs to write these.\n\n" +
                    "STEP 3 — Ask the editor what to do next.\n" +
                    "Option A: 'I'll apply the text fields automatically — just confirm.' " +
                    "  → For each simple text field (TextBox, TextArea, Tiptap) attempt to set the value. " +
                    "  → Stop after each field and confirm before continuing.\n" +
                    "Option B: 'Show me the content to copy.' " +
                    "  → Print each mapped field value, formatted and ready to paste.\n\n" +
                    "HARD RULES:\n" +
                    "- Image pickers, media pickers and block editors: always flag as MANUAL. " +
                    "  Do not attempt to set these — they will fail.\n" +
                    "- Never save or publish without the editor saying 'save' or 'publish'.\n" +
                    "- Backoffice links use this format: " +
                    "  https://localhost:44392/umbraco/section/content/workspace/document/edit/{nodeKey}/en-US/\n\n" +
                    "For external document import (Word, PDF, etc.):\n" +
                    "- Binary files cannot be read — ask the editor to paste the text into this chat.\n" +
                    "- Public URLs can be fetched — ask for the URL and read the page.\n" +
                    "- Once you have the text, follow the same mapping/report flow as above.",
                AllowedToolScopeIds = allToolScopeIds
            },
            IsActive = true
        }, ct);

        // Note: translation is handled by the "Translate Node" workspace view tab
        // registered by Limbo.Umbraco.AI via AddLimboAiTranslation() in AiSeedComposer.
        // No Copilot translation agent is needed.
    }

    /// <summary>
    /// Runs on every startup when the connection already exists.
    /// Adds any prompts or agents that were introduced after the initial seed,
    /// without touching anything that already exists.
    /// </summary>
    private async Task EnsureNewItemsAsync(CancellationToken ct)
    {
        var profile = await profileService.GetProfileByAliasAsync("testsite-chat", ct);
        if (profile is null)
        {
            logger.LogWarning("[AI Seed] testsite-chat profile not found — cannot ensure new items.");
            return;
        }

        var context = await contextService.GetContextByAliasAsync("testsite-context", ct);
        if (context is null)
        {
            logger.LogWarning("[AI Seed] testsite-context not found — cannot ensure new items.");
            return;
        }

        var allToolScopeIds = toolScopes.Select(x => x.Id).ToArray();

        // Scope sets used by new prompts
        var shortTextEditors = new[]
        {
            "Umb.PropertyEditorUi.TextArea",
            "Umb.PropertyEditorUi.TextBox",
        };

        var allTextEditors = new[]
        {
            "Umb.PropertyEditorUi.TextArea",
            "Umb.PropertyEditorUi.TextBox",
            "Umb.PropertyEditorUi.Tiptap",
        };

        // ── Alt text scope fix ────────────────────────────────────────────────
        // v1: incorrectly included MediaPicker → corrupted image slots.
        // v2: TextBox-only but no content-type or property-alias constraint
        //     → appeared on every TextBox in the backoffice (Title, Width, etc.).
        // v3: locked to ContentTypeAlias="Image", PropertyAlias="altText".
        // v4 (current): two rules — Image/altText AND testArticle/imageAlt.
        var altText = await promptService.GetPromptByAliasAsync("generate-alt-text", ct);
        var altTextNeedsScope = altText is not null && (
            altText.Scope?.AllowRules?.Any(r =>
                r.PropertyEditorUiAliases?.Contains("Umb.PropertyEditorUi.MediaPicker") == true) == true ||
            altText.Scope?.AllowRules?.Any(r =>
                r.ContentTypeAliases?.Contains("Image") == true &&
                r.PropertyAliases?.Contains("altText") == true) != true ||
            altText.Scope?.AllowRules?.Any(r =>
                r.ContentTypeAliases?.Contains("testArticle") == true &&
                r.PropertyAliases?.Contains("imageAlt") == true) != true);

        if (altTextNeedsScope)
        {
            logger.LogInformation("[AI Seed] Patching generate-alt-text scope to Image/altText + testArticle/imageAlt…");
            altText!.Scope = new AIPromptScope
            {
                AllowRules =
                [
                    new AIPromptScopeRule
                    {
                        ContentTypeAliases      = ["Image"],
                        PropertyAliases         = ["altText"],
                        PropertyEditorUiAliases = ["Umb.PropertyEditorUi.TextBox"]
                    },
                    new AIPromptScopeRule
                    {
                        ContentTypeAliases      = ["testArticle"],
                        PropertyAliases         = ["imageAlt"],
                        PropertyEditorUiAliases = ["Umb.PropertyEditorUi.TextBox"]
                    }
                ]
            };
            await promptService.SavePromptAsync(altText, ct);
        }

        // ── Schema.org instructions patch ─────────────────────────────────────
        // Earlier seeds had instructions that let Gemini call list_context_resources,
        // which fails with ArgumentException: missing 'args' parameter.
        // Patch any existing record to prepend the "do not call tools" guard.
        const string schemaOrgGuard = "You already have all the context you need in the entity information provided. Do not call any additional tools or fetch any external resources.";
        var schemaOrg = await promptService.GetPromptByAliasAsync("schema-org-jsonld", ct);
        if (schemaOrg is not null && !schemaOrg.Instructions.Contains(schemaOrgGuard))
        {
            logger.LogInformation("[AI Seed] Patching schema-org-jsonld instructions to suppress list_context_resources calls…");
            schemaOrg.Instructions = schemaOrgGuard + "\n\n" + schemaOrg.Instructions;
            await promptService.SavePromptAsync(schemaOrg, ct);
        }

        // ── Prompts: ensure each is present ──────────────────────────────────

        await EnsurePromptAsync("generate-alt-text", ct, new AIPrompt
        {
            Alias = "generate-alt-text",
            Name = "Generate alt text",
            Description = "Generates descriptive alt text for the associated image",
            Instructions =
                "Describe the image for screen readers. Be concise and accurate. " +
                "Do not start with 'Image of' or 'Photo of'. " +
                "Return only the alt text — no explanation or extra text.",
            ProfileId = profile.Id,
            IsActive = true,
            IncludeEntityContext = true,
            Scope = new AIPromptScope
            {
                AllowRules =
                [
                    new AIPromptScopeRule
                    {
                        ContentTypeAliases      = ["Image"],
                        PropertyAliases         = ["altText"],
                        PropertyEditorUiAliases = ["Umb.PropertyEditorUi.TextBox"]
                    },
                    new AIPromptScopeRule
                    {
                        ContentTypeAliases      = ["testArticle"],
                        PropertyAliases         = ["imageAlt"],
                        PropertyEditorUiAliases = ["Umb.PropertyEditorUi.TextBox"]
                    }
                ]
            }
        });

        await EnsurePromptAsync("seo-meta-title", ct, new AIPrompt
        {
            Alias = "seo-meta-title",
            Name = "SEO: Meta title",
            Description = "Generate an SEO-optimised page title (max 60 characters)",
            Instructions =
                "Based on the page content, write an SEO-optimised page title. " +
                "Maximum 60 characters. Include the primary keyword naturally. " +
                "Start with the main topic. Do not append the site name. " +
                "Return only the title.",
            ProfileId = profile.Id,
            IsActive = true,
            IncludeEntityContext = true,
            OptionCount = 3,
            Scope = new AIPromptScope
            {
                AllowRules = [new AIPromptScopeRule { PropertyEditorUiAliases = shortTextEditors }]
            }
        });

        await EnsurePromptAsync("seo-meta-description", ct, new AIPrompt
        {
            Alias = "seo-meta-description",
            Name = "SEO: Meta description",
            Description = "Generate an SEO-optimised meta description (150–160 characters)",
            Instructions =
                "Based on the page content, write an SEO-optimised meta description. " +
                "150–160 characters. Include relevant keywords naturally. " +
                "Invite the reader to click. Write in clear, accessible language. " +
                "Return only the description.",
            ProfileId = profile.Id,
            IsActive = true,
            IncludeEntityContext = true,
            OptionCount = 3,
            Scope = new AIPromptScope
            {
                AllowRules = [new AIPromptScopeRule { PropertyEditorUiAliases = shortTextEditors }]
            }
        });

        await EnsurePromptAsync("schema-org-jsonld", ct, new AIPrompt
        {
            Alias = "schema-org-jsonld",
            Name = "Schema.org JSON-LD",
            Description = "Generate Schema.org structured data for this page",
            Instructions =
                schemaOrgGuard + "\n\n" +
                "Based on the entity context, generate appropriate Schema.org JSON-LD structured data. " +
                "Select the most relevant Schema.org type for this content " +
                "(e.g. WebPage, Article, FAQPage, Organization, BreadcrumbList). " +
                "Populate all properties that can be inferred from the entity context. " +
                "Return only the JSON-LD object — no surrounding <script> tags, no explanation. " +
                "The output must be valid JSON.",
            ProfileId = profile.Id,
            IsActive = true,
            IncludeEntityContext = true,
            OptionCount = 1,
            Scope = new AIPromptScope
            {
                AllowRules =
                [
                    new AIPromptScopeRule
                    {
                        PropertyEditorUiAliases = ["Umb.PropertyEditorUi.TextArea"]
                    }
                ]
            }
        });

        // ── Stale prompt cleanup ──────────────────────────────────────────────
        // These per-property prompts were replaced by the Copilot agents
        // (translation-assistant, content-assistant). Auto-delete them so they
        // no longer appear in the property editor context menu.
        var stalePromptAliases = new[]
        {
            "tone-formal",
            "tone-simplified",
            "translate-to-english",
            "translate-to-danish",
        };
        foreach (var staleAlias in stalePromptAliases)
        {
            var stale = await promptService.GetPromptByAliasAsync(staleAlias, ct);
            if (stale is null) continue;
            logger.LogInformation("[AI Seed] Removing stale prompt '{Alias}'…", staleAlias);
            await promptService.DeletePromptAsync(stale.Id, ct);
        }

        await EnsurePromptAsync("structure-document", ct, new AIPrompt
        {
            Alias = "structure-document",
            Name = "Structure document content",
            Description = "Restructure pasted document text to fit this field",
            Instructions =
                "The current field contains raw text copied from an external document " +
                "(e.g. a Word file, PDF or email). " +
                "Using the entity context to understand the purpose of this field, " +
                "rewrite and restructure the pasted text so it reads as polished, " +
                "publication-ready content appropriate for this field. " +
                "Preserve all factual information. Remove document artefacts " +
                "(headers, page numbers, formatting codes). " +
                "Return only the restructured text.\n\n" +
                "Pasted content:\n{{currentValue}}",
            ProfileId = profile.Id,
            IsActive = true,
            IncludeEntityContext = true,
            OptionCount = 1,
            Scope = new AIPromptScope
            {
                AllowRules =
                [
                    new AIPromptScopeRule
                    {
                        PropertyEditorUiAliases =
                        [
                            "Umb.PropertyEditorUi.TextArea",
                            "Umb.PropertyEditorUi.Tiptap",
                        ]
                    }
                ]
            }
        });

        // ── Agents: ensure each is present ────────────────────────────────────

        await EnsureAgentAsync("content-assistant", ct, new AIAgent
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
                    "Umbraco 17 format: https://localhost:44392/umbraco/section/content/workspace/document/edit/{nodeKey}/en-US/ " +
                    "Use the nodeKey (GUID) returned by get_umbraco_content. " +
                    "Do NOT use the old #/content/content/edit/ format.",
                AllowedToolScopeIds = allToolScopeIds
            },
            IsActive = true
        });

        await EnsureAgentAsync("media-assistant", ct, new AIAgent
        {
            Alias = "media-assistant",
            Name = "Media Assistant",
            Description = "Helps write alt text and captions for images and media",
            ProfileId = profile.Id,
            SurfaceIds = ["copilot"],
            Scope = new AIAgentScope
            {
                AllowRules = [new AIAgentScopeRule { Sections = ["media"] }]
            },
            Config = new AIStandardAgentConfig
            {
                ContextIds = [context.Id],
                Instructions =
                    "You are a media assistant for this Umbraco test site. " +
                    "Help editors write accurate, descriptive alt text for images and other media. " +
                    "Focus on accessibility: describe visual content precisely for " +
                    "people using screen readers. Keep alt text concise and meaningful. " +
                    "Do not start descriptions with 'Image of' or 'Photo of'.",
                AllowedToolScopeIds = allToolScopeIds
            },
            IsActive = true
        });

        await EnsureAgentAsync("migration-assistant", ct, new AIAgent
        {
            Alias = "migration-assistant",
            Name = "Migration Assistant",
            Description = "Reads two content nodes and produces a field-by-field migration plan",
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
                    "You are a content migration analyst for this Umbraco site.\n\n" +
                    "Your job: read a SOURCE content node and a TARGET content node (or document type), " +
                    "then produce a precise migration plan the editor can act on immediately.\n\n" +
                    "STEP 1 — Identify source and target.\n" +
                    "The currently open page (entity context) is the source by default. " +
                    "Ask the editor for the target node name, URL or key if they haven't given it. " +
                    "Use get_umbraco_content to read both nodes. " +
                    "Use get_content_type_schema to understand the target document type structure.\n\n" +
                    "STEP 2 — Produce a migration report with these three sections:\n\n" +
                    "## Field mapping\n" +
                    "A table: Source field | Current value (truncated) | Target field | Action\n" +
                    "Action is one of: Copy as-is / Rewrite / Trim to N chars / Manual (too complex)\n\n" +
                    "## Content that needs rewriting\n" +
                    "Source content that doesn't fit the target field directly — show the editor what " +
                    "needs adapting and suggest how.\n\n" +
                    "## Gaps in the target\n" +
                    "Target fields that have no matching source content — the editor needs to write these.\n\n" +
                    "STEP 3 — Ask the editor what to do next.\n" +
                    "Option A: 'I'll apply the text fields automatically — just confirm.' " +
                    "  → For each simple text field (TextBox, TextArea, Tiptap) attempt to set the value. " +
                    "  → Stop after each field and confirm before continuing.\n" +
                    "Option B: 'Show me the content to copy.' " +
                    "  → Print each mapped field value, formatted and ready to paste.\n\n" +
                    "HARD RULES:\n" +
                    "- Image pickers, media pickers and block editors: always flag as MANUAL. " +
                    "  Do not attempt to set these — they will fail.\n" +
                    "- Never save or publish without the editor saying 'save' or 'publish'.\n" +
                    "- Backoffice links use this format: " +
                    "  https://localhost:44392/umbraco/section/content/workspace/document/edit/{nodeKey}/en-US/\n\n" +
                    "For external document import (Word, PDF, etc.):\n" +
                    "- Binary files cannot be read — ask the editor to paste the text into this chat.\n" +
                    "- Public URLs can be fetched — ask for the URL and read the page.\n" +
                    "- Once you have the text, follow the same mapping/report flow as above.",
                AllowedToolScopeIds = allToolScopeIds
            },
            IsActive = true
        });

        // ── Stale agent cleanup ───────────────────────────────────────────────
        // Translation is now handled by the "Translate Node" workspace view tab
        // provided by the Limbo.Umbraco.AI package (AddLimboAiTranslation).
        // Remove any previously seeded translation-assistant Copilot agent.
        var staleTranslationAgent = await agentService.GetAgentByAliasAsync("translation-assistant", ct);
        if (staleTranslationAgent is not null)
        {
            logger.LogInformation("[AI Seed] Removing stale translation-assistant Copilot agent…");
            await agentService.DeleteAgentAsync(staleTranslationAgent.Id, ct);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds the given prompt if no prompt with <paramref name="alias"/> exists yet.
    /// No-ops silently if the prompt is already present.
    /// </summary>
    private async Task EnsurePromptAsync(string alias, CancellationToken ct, AIPrompt prompt)
    {
        var existing = await promptService.GetPromptByAliasAsync(alias, ct);
        if (existing is not null)
        {
            logger.LogDebug("[AI Seed] Prompt '{Alias}' already exists — skipping.", alias);
            return;
        }

        logger.LogInformation("[AI Seed] Seeding prompt '{Alias}'…", alias);
        await promptService.SavePromptAsync(prompt, ct);
    }

    /// <summary>
    /// Seeds the given agent if no agent with <paramref name="alias"/> exists yet.
    /// No-ops silently if the agent is already present.
    /// </summary>
    private async Task EnsureAgentAsync(string alias, CancellationToken ct, AIAgent agent)
    {
        var existing = await agentService.GetAgentByAliasAsync(alias, ct);
        if (existing is not null)
        {
            logger.LogDebug("[AI Seed] Agent '{Alias}' already exists — skipping.", alias);
            return;
        }

        logger.LogInformation("[AI Seed] Seeding agent '{Alias}'…", alias);
        await agentService.SaveAgentAsync(agent, ct);
    }

    private static JsonElement ToJsonElement(object value)
    {
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }
}
