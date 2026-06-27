using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;

namespace Umbraco.AI.Testsite;

/// <summary>
/// Bootstraps a minimal document type hierarchy and seeds content on first startup.
/// Idempotent: checks for the home doc type before creating anything.
/// </summary>
public sealed class ContentBootstrapHandler(
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
            await EnsureDocTypesAsync();
            await SeedContentAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Content Bootstrap] Skipped (database not ready yet): {Message}", ex.Message);
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
            Variations  = ContentVariation.Nothing,
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
            Variations  = ContentVariation.Nothing,
        };
        contentPage.AddPropertyGroup("content", "Content");
        AddProp(contentPage, shortStringHelper, textstring, "headline", "Headline", "content");
        AddProp(contentPage, shortStringHelper, richtext,   "body",     "Body",     "content");
        await contentTypeService.CreateAsync(contentPage, Constants.Security.SuperUserKey);

        // ── Test Home Page ────────────────────────────────────────────────────
        var home = new ContentType(shortStringHelper, -1)
        {
            Key          = HomeKey,
            Alias        = HomeAlias,
            Name         = "Test Home Page",
            Description  = "Site root for the AI test site.",
            Icon         = "icon-home",
            AllowedAsRoot = true,
            Variations   = ContentVariation.Nothing,
        };
        home.AddPropertyGroup("hero", "Hero");
        AddProp(home, shortStringHelper, textstring, "headline",  "Headline",  "hero");
        AddProp(home, shortStringHelper, textarea,   "introText", "Intro text","hero");
        home.AllowedContentTypes =
        [
            new ContentTypeSort(article.Key,     0, ArticleAlias),
            new ContentTypeSort(contentPage.Key, 1, ContentPageAlias),
        ];
        await contentTypeService.CreateAsync(home, Constants.Security.SuperUserKey);

        logger.LogInformation("[Content Bootstrap] Document types created.");
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

        var home = Create("AI Test Site", -1, HomeAlias, c =>
        {
            c.SetValue("headline",  "AI Test Site");
            c.SetValue("introText", "A minimal Umbraco site for testing the Limbo.Umbraco.AI package.");
        });

        Create("Getting Started with Umbraco AI", home.Id, ArticleAlias, c =>
        {
            c.SetValue("title",   "Getting Started with Umbraco AI");
            c.SetValue("summary", "An introduction to using AI-powered features inside the Umbraco backoffice.");
            c.SetValue("body",    "<p>Umbraco.AI adds a set of AI features directly to the Umbraco backoffice. " +
                                  "Editors can use prompts to rewrite and summarise content, and the Copilot agent " +
                                  "can help navigate and edit content nodes using natural language.</p>");
        });

        Create("Content Strategy Tips", home.Id, ArticleAlias, c =>
        {
            c.SetValue("title",   "Content Strategy Tips");
            c.SetValue("summary", "Practical tips for planning and maintaining high-quality content in a CMS.");
            c.SetValue("body",    "<p>A good content strategy starts with understanding your audience. " +
                                  "Define clear goals for each page, use consistent terminology, and review " +
                                  "content regularly to keep it accurate and up to date.</p>" +
                                  "<p>Use structured document types to enforce consistency across editors and " +
                                  "make AI-assisted editing more predictable.</p>");
        });

        Create("Writing for the Web", home.Id, ArticleAlias, c =>
        {
            c.SetValue("title",   "Writing for the Web");
            c.SetValue("summary", "How to write clear, scannable content that works on screen.");
            c.SetValue("body",    "<p>Online readers scan rather than read. Use short paragraphs, descriptive " +
                                  "headings, and plain language. Front-load the most important information and " +
                                  "cut anything that does not serve the reader.</p>");
        });

        Create("About This Site", home.Id, ContentPageAlias, c =>
        {
            c.SetValue("headline", "About This Site");
            c.SetValue("body",     "<p>This site is a test vehicle for the <strong>Limbo.Umbraco.AI</strong> package. " +
                                   "It provides a realistic content tree so that the Copilot agent and prompt features " +
                                   "can be exercised against real nodes.</p>");
        });

        Create("Contact", home.Id, ContentPageAlias, c =>
        {
            c.SetValue("headline", "Contact");
            c.SetValue("body",     "<p>This is a placeholder contact page. Use it to test how the AI rewrites " +
                                   "or summarises short-form content.</p>");
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
        configure(content);
        contentService.Save(content);
        contentService.Publish(content, []);
        return content;
    }

    private static void AddProp(
        ContentType ct,
        IShortStringHelper helper,
        IDataType dataType,
        string alias,
        string name,
        string groupAlias)
    {
        var pt = new PropertyType(helper, dataType, alias)
        {
            Name       = name,
            Mandatory  = false,
            Variations = ContentVariation.Nothing,
        };
        ct.AddPropertyType(pt, groupAlias);
    }
}
