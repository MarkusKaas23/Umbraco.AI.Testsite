using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;

namespace Umbraco.AI.Testsite;

/// <summary>
/// Bootstraps a minimal document type hierarchy and seeds Danish content on first startup.
/// Doc types use <see cref="ContentVariation.Culture"/> so each content node can hold
/// multiple language versions. The primary culture is da-DK; the "Translate Node" workspace
/// view can translate it to any of the registered target languages (en-US, de-DE, fr-FR,
/// es-ES) as unpublished draft culture variants on the same node.
/// </summary>
public sealed class ContentBootstrapHandler(
    ILanguageService languageService,
    IContentTypeService contentTypeService,
    IDataTypeService dataTypeService,
    IContentService contentService,
    IDatabaseCacheRebuilder cacheRebuilder,
    IShortStringHelper shortStringHelper,
    IRuntimeState runtimeState,
    ILogger<ContentBootstrapHandler> logger)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    // ── Doc type aliases ──────────────────────────────────────────────────────
    private const string HomeAlias        = "testHomePage";
    private const string ArticleAlias     = "testArticle";
    private const string ContentPageAlias = "testContentPage";

    // ── Primary content culture ───────────────────────────────────────────────
    private const string PrimaryCulture = "da-DK";

    // ── Fixed keys so the types are stable across reinstalls ─────────────────
    private static readonly Guid HomeKey        = new("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");
    private static readonly Guid ArticleKey     = new("B2C3D4E5-F601-7890-BCDE-F12345678901");
    private static readonly Guid ContentPageKey = new("C3D4E5F6-0712-8901-CDEF-012345678902");

    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken ct)
    {
        if (runtimeState.Level != RuntimeLevel.Run)
            return;

        try
        {
            await EnsureLanguagesAsync(ct);
            await EnsureTargetLanguagesAsync();
            await EnsureDocTypesAsync();
            await EnsureArticleHasImagePropertyAsync();
            await SeedContentAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Content Bootstrap] Skipped (database not ready yet): {Message}", ex.Message);
        }
    }

    // ── Languages ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Ensures da-DK exists and is the default language.
    /// Umbraco installs with en-US as default; we promote da-DK so the backoffice
    /// language selector defaults to Danish on every content node.
    /// </summary>
    private async Task EnsureLanguagesAsync(CancellationToken ct)
    {
        var existing = await languageService.GetAsync(PrimaryCulture);
        if (existing is not null)
        {
            if (!existing.IsDefault)
            {
                existing.IsDefault   = true;
                existing.IsMandatory = true;
                var upd = await languageService.UpdateAsync(existing, Constants.Security.SuperUserKey);
                if (upd.Success)
                    logger.LogInformation("[Content Bootstrap] Promoted {Culture} to default language.", PrimaryCulture);
                else
                    logger.LogWarning("[Content Bootstrap] Could not promote {Culture}: {Status}", PrimaryCulture, upd.Status);
            }
            return;
        }

        // Step 1 — create da-DK as non-default to avoid "two-default" conflict.
        var lang = new Language(PrimaryCulture, "Danish (Denmark)")
        {
            IsDefault    = false,
            IsMandatory  = true,
        };

        var createResult = await languageService.CreateAsync(lang, Constants.Security.SuperUserKey);
        if (!createResult.Success)
        {
            logger.LogWarning("[Content Bootstrap] Could not create {Culture}: {Status}", PrimaryCulture, createResult.Status);
            return;
        }

        logger.LogInformation("[Content Bootstrap] Created {Culture} language.", PrimaryCulture);

        // Step 2 — promote to default; Umbraco atomically demotes en-US.
        var created = createResult.Result!;
        created.IsDefault = true;

        var promoteResult = await languageService.UpdateAsync(created, Constants.Security.SuperUserKey);
        if (!promoteResult.Success)
            logger.LogWarning("[Content Bootstrap] Could not promote {Culture} to default: {Status}", PrimaryCulture, promoteResult.Status);
        else
            logger.LogInformation("[Content Bootstrap] {Culture} set as default language.", PrimaryCulture);
    }

    // ── Translation target languages ──────────────────────────────────────────

    private static readonly (string Culture, string DisplayName)[] TranslationTargets =
    [
        ("en-US", "English (United States)"),
        ("de-DE", "German (Germany)"),
        ("fr-FR", "French (France)"),
        ("es-ES", "Spanish (Spain)"),
    ];

    /// <summary>
    /// Ensures all translation target languages are registered in Umbraco.
    /// en-US is typically present by default; de-DE / fr-FR / es-ES are added if missing.
    /// These must exist before <see cref="TranslationWorker"/> can write culture variants.
    /// </summary>
    private async Task EnsureTargetLanguagesAsync()
    {
        foreach (var (culture, displayName) in TranslationTargets)
        {
            if (await languageService.GetAsync(culture) is not null)
                continue;

            var lang = new Language(culture, displayName)
            {
                IsDefault   = false,
                IsMandatory = false,
            };

            var result = await languageService.CreateAsync(lang, Constants.Security.SuperUserKey);
            if (result.Success)
                logger.LogInformation("[Content Bootstrap] Registered translation language {Culture}.", culture);
            else
                logger.LogWarning("[Content Bootstrap] Could not register {Culture}: {Status}", culture, result.Status);
        }
    }

    // ── Document types ────────────────────────────────────────────────────────

    private async Task EnsureDocTypesAsync()
    {
        if (contentTypeService.Get(HomeAlias) is not null)
        {
            logger.LogDebug("[Content Bootstrap] Doc types already exist — skipping.");
            return;
        }

        logger.LogInformation("[Content Bootstrap] Creating document types…");

        var textstring = (await dataTypeService.GetByEditorAliasAsync("Umbraco.TextBox")).First();
        var textarea   = (await dataTypeService.GetByEditorAliasAsync("Umbraco.TextArea")).First();
        var richtext   = (await dataTypeService.GetByEditorAliasAsync("Umbraco.RichText")).First();

        // ── Test Article ──────────────────────────────────────────────────────
        var article = new ContentType(shortStringHelper, -1)
        {
            Key         = ArticleKey,
            Alias       = ArticleAlias,
            Name        = "Test Article",
            Description = "A simple article used to test the Umbraco.AI features.",
            Icon        = "icon-newspaper",
            Variations  = ContentVariation.Culture,
        };
        article.AddPropertyGroup("content", "Content");
        AddProp(article, shortStringHelper, textstring, "title",   "Title",   "content");
        AddProp(article, shortStringHelper, textarea,   "summary", "Summary", "content");
        AddProp(article, shortStringHelper, richtext,   "body",    "Body",    "content");
        await contentTypeService.CreateAsync(article, Constants.Security.SuperUserKey);

        // ── Test Content Page ─────────────────────────────────────────────────
        var contentPage = new ContentType(shortStringHelper, -1)
        {
            Key         = ContentPageKey,
            Alias       = ContentPageAlias,
            Name        = "Test Content Page",
            Description = "A generic content page for testing.",
            Icon        = "icon-document",
            Variations  = ContentVariation.Culture,
        };
        contentPage.AddPropertyGroup("content", "Content");
        AddProp(contentPage, shortStringHelper, textstring, "headline", "Headline", "content");
        AddProp(contentPage, shortStringHelper, richtext,   "body",     "Body",     "content");
        await contentTypeService.CreateAsync(contentPage, Constants.Security.SuperUserKey);

        // ── Test Home Page ────────────────────────────────────────────────────
        var home = new ContentType(shortStringHelper, -1)
        {
            Key           = HomeKey,
            Alias         = HomeAlias,
            Name          = "Test Home Page",
            Description   = "Site root for the AI test site.",
            Icon          = "icon-home",
            AllowedAsRoot = true,
            Variations    = ContentVariation.Culture,
        };
        home.AddPropertyGroup("hero", "Hero");
        AddProp(home, shortStringHelper, textstring, "headline",  "Headline",   "hero");
        AddProp(home, shortStringHelper, textarea,   "introText", "Intro text", "hero");
        home.AllowedContentTypes =
        [
            new ContentTypeSort(article.Key,     0, ArticleAlias),
            new ContentTypeSort(contentPage.Key, 1, ContentPageAlias),
        ];
        await contentTypeService.CreateAsync(home, Constants.Security.SuperUserKey);

        logger.LogInformation("[Content Bootstrap] Document types created.");
    }

    // ── Content type migrations ───────────────────────────────────────────────

    /// <summary>
    /// Adds an Image (media picker) and Alt text (textbox) property to the Test Article
    /// doc type if they are not already present. Safe to call on an existing database.
    /// </summary>
    private async Task EnsureArticleHasImagePropertyAsync()
    {
        if (contentTypeService.Get(ArticleAlias) is not ContentType article)
            return;

        if (article.PropertyTypes.Any(p => p.Alias == "image"))
            return;

        var textstring  = (await dataTypeService.GetByEditorAliasAsync("Umbraco.TextBox")).First();
        var mediaPicker = (await dataTypeService.GetByEditorAliasAsync("Umbraco.MediaPicker3")).First();

        // Image picker: invariant — the same image is used for all languages.
        // Alt text: culture-variant — translated alongside the other text fields.
        AddProp(article, shortStringHelper, mediaPicker, "image",    "Image",    "content", ContentVariation.Nothing);
        AddProp(article, shortStringHelper, textstring,  "imageAlt", "Alt text", "content", ContentVariation.Culture);

        await contentTypeService.UpdateAsync(article, Constants.Security.SuperUserKey);
        logger.LogInformation("[Content Bootstrap] Added Image + Alt text properties to Test Article.");
    }

    // ── Content seed ──────────────────────────────────────────────────────────

    private async Task SeedContentAsync()
    {
        if (contentService.GetRootContent().Any())
        {
            logger.LogDebug("[Content Bootstrap] Content already exists — skipping seed.");
            return;
        }

        if (contentTypeService.Get(HomeAlias) is null)
        {
            logger.LogWarning("[Content Bootstrap] Home doc type missing — cannot seed content.");
            return;
        }

        logger.LogInformation("[Content Bootstrap] Seeding content…");

        // All seed content is in Danish (da-DK). Translations to other languages are created
        // on demand via the "Translate Node" tab — each translation adds a culture variant
        // as an unpublished draft on the same node (en-US, de-DE, fr-FR, or es-ES).
        var home = Create("AI Testsite", -1, HomeAlias, c =>
        {
            c.SetValue("headline",  "AI Testsite",                                                  culture: PrimaryCulture);
            c.SetValue("introText", "En minimal Umbraco-side til test af pakken Limbo.Umbraco.AI.", culture: PrimaryCulture);
        });

        Create("Kom godt i gang med Umbraco AI", home.Id, ArticleAlias, c =>
        {
            c.SetValue("title",   "Kom godt i gang med Umbraco AI",                                                  culture: PrimaryCulture);
            c.SetValue("summary", "En introduktion til AI-drevne funktioner direkte i Umbraco-backoffice.",           culture: PrimaryCulture);
            c.SetValue("body",
                "<p>Umbraco.AI tilføjer en række AI-funktioner direkte i Umbraco-backoffice. " +
                "Redaktører kan bruge prompter til at omskrive og opsummere indhold, og Copilot-agenten " +
                "kan hjælpe med at navigere og redigere indholdsnoder via naturligt sprog.</p>",                      culture: PrimaryCulture);
        });

        Create("Tips til indholdsstrategi", home.Id, ArticleAlias, c =>
        {
            c.SetValue("title",   "Tips til indholdsstrategi",                                                        culture: PrimaryCulture);
            c.SetValue("summary", "Praktiske råd til planlægning og vedligeholdelse af kvalitetsindhold i et CMS.",   culture: PrimaryCulture);
            c.SetValue("body",
                "<p>En god indholdsstrategi starter med at forstå din målgruppe. " +
                "Definer klare mål for hver side, brug ensartet terminologi og gennemgå " +
                "indholdet regelmæssigt for at holde det præcist og opdateret.</p>" +
                "<p>Brug strukturerede dokumenttyper for at sikre ensartethed på tværs af redaktører og " +
                "gøre AI-assisteret redigering mere forudsigelig.</p>",                                               culture: PrimaryCulture);
        });

        Create("Skrivning til nettet", home.Id, ArticleAlias, c =>
        {
            c.SetValue("title",   "Skrivning til nettet",                                                              culture: PrimaryCulture);
            c.SetValue("summary", "Sådan skriver du klart og scannbart indhold, der fungerer på skærmen.",             culture: PrimaryCulture);
            c.SetValue("body",
                "<p>Onlinelæsere scanner snarere end læser. Brug korte afsnit, beskrivende " +
                "overskrifter og klart sprog. Placer de vigtigste oplysninger øverst og " +
                "fjern alt, der ikke tjener læseren.</p>",                                                             culture: PrimaryCulture);
        });

        Create("Om denne side", home.Id, ContentPageAlias, c =>
        {
            c.SetValue("headline", "Om denne side",                                                                    culture: PrimaryCulture);
            c.SetValue("body",
                "<p>Denne side er et testmiljø for pakken <strong>Limbo.Umbraco.AI</strong>. " +
                "Den indeholder et realistisk indholdstræ, så Copilot-agenten og promptfunktionerne " +
                "kan afprøves på rigtige noder.</p>",                                                                  culture: PrimaryCulture);
        });

        Create("Kontakt", home.Id, ContentPageAlias, c =>
        {
            c.SetValue("headline", "Kontakt",                                                                          culture: PrimaryCulture);
            c.SetValue("body",
                "<p>Dette er en placeholder-kontaktside. Brug den til at teste, hvordan AI'en " +
                "omskriver eller opsummerer kortere indhold.</p>",                                                     culture: PrimaryCulture);
        });

        logger.LogInformation("[Content Bootstrap] Content seeded: home + 3 articles + 2 content pages.");

        logger.LogInformation("[Content Bootstrap] Rebuilding published-content cache…");
        await cacheRebuilder.RebuildAsync(false);
        logger.LogInformation("[Content Bootstrap] Cache rebuilt.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private IContent Create(string name, int parentId, string contentTypeAlias, Action<IContent> configure)
    {
        var content = contentService.Create(name, parentId, contentTypeAlias);
        content.SetCultureName(name, PrimaryCulture);
        configure(content);
        contentService.Save(content);
        contentService.Publish(content, [PrimaryCulture]);
        return content;
    }

    private static void AddProp(
        ContentType ct,
        IShortStringHelper helper,
        IDataType dataType,
        string alias,
        string name,
        string groupAlias,
        ContentVariation variation = ContentVariation.Culture)
    {
        var pt = new PropertyType(helper, dataType, alias)
        {
            Name       = name,
            Mandatory  = false,
            Variations = variation,
        };
        ct.AddPropertyType(pt, groupAlias);
    }
}
