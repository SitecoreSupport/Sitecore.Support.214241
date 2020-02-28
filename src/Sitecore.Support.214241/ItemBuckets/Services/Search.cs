namespace Support.ItemBuckets.Services
{
    using Newtonsoft.Json;
    using Sitecore;
    using Sitecore.Buckets.Caching;
    using Sitecore.Buckets.Extensions;
    using Sitecore.Buckets.Pipelines.Search.GetFacets;
    using Sitecore.Buckets.Pipelines.UI.FetchContextData;
    using Sitecore.Buckets.Pipelines.UI.FetchContextView;
    using Sitecore.Buckets.Pipelines.UI.FillItem;
    using Sitecore.Buckets.Pipelines.UI.Search;
    using Sitecore.Buckets.Search;
    using Sitecore.Buckets.Util;
    using Sitecore.Caching;
    using Sitecore.Configuration;
    using Sitecore.ContentSearch;
    using Sitecore.ContentSearch.Diagnostics;
    using Sitecore.ContentSearch.Exceptions;
    using Sitecore.ContentSearch.Linq;
    using Sitecore.ContentSearch.Linq.Parsing;
    using Sitecore.ContentSearch.SearchTypes;
    using Sitecore.Data;
    using Sitecore.Data.Fields;
    using Sitecore.Data.Items;
    using Sitecore.Globalization;
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Collections.Specialized;
    using System.Diagnostics;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using System.Web;
    using System.Web.SessionState;

    [UsedImplicitly]
    public class Search : SearchHttpTaskAsyncHandler, IRequiresSessionState
    {
        #region Original code

        private static volatile Hashtable cacheHashtable;

        private static readonly object ThisLock = new object();

        public override bool IsReusable => false;

        private static Hashtable CacheHashTable
        {
            get
            {
                if (cacheHashtable == null)
                {
                    lock (ThisLock)
                    {
                        if (cacheHashtable == null)
                        {
                            cacheHashtable = new Hashtable();
                        }
                    }
                }
                return cacheHashtable;
            }
        }

        public override void ProcessRequest(HttpContext context)
        {
        }
        
        private void PerformSearch(HttpContext context)
        {
            if (base.RunFacet)
            {
                try
                {
                    IEnumerable<IEnumerable<SitecoreUIFacet>> facets = GetFacetsPipeline.Run(new GetFacetsArgs(base.SearchQuery, base.LocationFilter));
                    FullSearch fullSearch = new FullSearch();
                    fullSearch.PageNumbers = 1;
                    fullSearch.facets = facets;
                    fullSearch.SearchCount = "1";
                    fullSearch.CurrentPage = 1;
                    FullSearch fullSearch2 = fullSearch;
                    string s = FormatCallbackString(fullSearch2);
                    context.Response.Write(s);
                }
                catch (IndexNotFoundException)
                {
                    FullSearch fullSearch3 = new FullSearch();
                    fullSearch3.PageNumbers = 0;
                    fullSearch3.facets = new List<IEnumerable<SitecoreUIFacet>>();
                    fullSearch3.SearchCount = "0";
                    fullSearch3.CurrentPage = 0;
                    FullSearch fullSearch4 = fullSearch3;
                    string s2 = FormatCallbackString(fullSearch4);
                    context.Response.Write(s2);
                }
                return;
            }
            Database database = base.Database.IsNullOrEmpty() ? Context.ContentDatabase : Factory.GetDatabase(base.Database);
            StoreUserContextSearches();
            SitecoreIndexableItem sitecoreIndexableItem = database.GetItem(base.LocationFilter);
            ISearchIndex searchIndex;
            try
            {
                searchIndex = (base.IndexName.IsEmpty() ? ContentSearchManager.GetIndex(sitecoreIndexableItem) : ContentSearchManager.GetIndex(base.IndexName));
            }
            catch (IndexNotFoundException)
            {
                SearchLog.Log.Warn("No index found for " + sitecoreIndexableItem.Item.ID);
                FullSearch fullSearch5 = new FullSearch();
                fullSearch5.PageNumbers = 0;
                fullSearch5.items = new List<UISearchResult>();
                fullSearch5.launchType = SearchHttpTaskAsyncHandler.GetEditorLaunchType();
                fullSearch5.SearchTime = base.SearchTime;
                fullSearch5.SearchCount = "0";
                fullSearch5.ContextData = new List<Tuple<View, object>>();
                fullSearch5.ContextDataView = new List<Tuple<int, View, string, IEnumerable<UISearchResult>>>();
                fullSearch5.CurrentPage = 0;
                fullSearch5.Location = ((Context.ContentDatabase.GetItem(base.LocationFilter) != null) ? Context.ContentDatabase.GetItem(base.LocationFilter).Name : Translate.Text("current item"));
                fullSearch5.ErrorMessage = Translate.Text("There are no results that match your search. Please try another search query or search in a different item.");
                FullSearch fullSearch6 = fullSearch5;
                string s3 = FormatCallbackString(fullSearch6);
                context.Response.Write(s3);
                return;
            }
            using (IProviderSearchContext context2 = searchIndex.CreateSearchContext())
            {
                int num = int.Parse(base.PageNumber);
                IEnumerable<UISearchResult> enumerable;
                int totalSearchResults;
                while (true)
                {
                    UISearchArgs uISearchArgs = new UISearchArgs(context2, base.SearchQuery, sitecoreIndexableItem);
                    uISearchArgs.Page = num - 1;
                    uISearchArgs.PageSize = base.ItemsPerPage;
                    UISearchArgs args = uISearchArgs;
                    base.Stopwatch.Start();
                    IQueryable<UISearchResult> queryable = UISearchPipeline.Run(args);
                    SearchResults<UISearchResult> results = queryable.GetResults();
                    enumerable = results.Hits.Select((SearchHit<UISearchResult> h) => h.Document);
                    if (BucketConfigurationSettings.EnableBucketDebug || Sitecore.Buckets.Util.Constants.EnableTemporaryBucketDebug)
                    {
                        SearchLog.Log.Info($"Search Query : {((IHasNativeQuery)queryable).Query}");
                        SearchLog.Log.Info($"Search Index : {searchIndex.Name}");
                    }
                    totalSearchResults = results.TotalSearchResults;
                    if (totalSearchResults != 0 || num == 1)
                    {
                        break;
                    }
                    num = 1;
                }
                List<UISearchResult> enumerableCollextion = enumerable.ToList();
                int num2 = (totalSearchResults % base.ItemsPerPage == 0) ? Math.Max(totalSearchResults / base.ItemsPerPage, 1) : (totalSearchResults / base.ItemsPerPage + 1);
                int num3 = (num - 1) * base.ItemsPerPage;
                if (num3 >= totalSearchResults)
                {
                    num = 1;
                }
                List<TemplateFieldItem> list = new List<TemplateFieldItem>();
                enumerableCollextion = ProcessCachedItems(enumerable, sitecoreIndexableItem, list, enumerableCollextion);
                if (base.IndexName == string.Empty)
                {
                    enumerableCollextion = enumerableCollextion.RemoveWhere((UISearchResult item) => item.Name == null || item.Content == null).ToList();
                }
                if (!BucketConfigurationSettings.SecuredItems.Equals("hide", StringComparison.InvariantCultureIgnoreCase))
                {
                    if (totalSearchResults > BucketConfigurationSettings.DefaultNumberOfResultsPerPage && enumerableCollextion.Count < BucketConfigurationSettings.DefaultNumberOfResultsPerPage && num <= num2)
                    {
                        while (enumerableCollextion.Count < BucketConfigurationSettings.DefaultNumberOfResultsPerPage)
                        {
                            enumerableCollextion.Add(new UISearchResult
                            {
                                ItemId = Guid.NewGuid().ToString()
                            });
                        }
                    }
                    else if (enumerableCollextion.Count < totalSearchResults && num == 1)
                    {
                        while (enumerableCollextion.Count < totalSearchResults && totalSearchResults < BucketConfigurationSettings.DefaultNumberOfResultsPerPage)
                        {
                            enumerableCollextion.Add(new UISearchResult
                            {
                                ItemId = Guid.NewGuid().ToString()
                            });
                        }
                    }
                }
                base.Stopwatch.Stop();
                IEnumerable<Tuple<View, object>> contextData = FetchContextDataPipeline.Run(new FetchContextDataArgs(base.SearchQuery, context2, sitecoreIndexableItem));
                IEnumerable<Tuple<int, View, string, IEnumerable<UISearchResult>>> contextDataView = FetchContextViewPipeline.Run(new FetchContextViewArgs(base.SearchQuery, context2, sitecoreIndexableItem, list));
                FullSearch fullSearch7 = new FullSearch();
                fullSearch7.PageNumbers = num2;
                fullSearch7.items = enumerableCollextion;
                fullSearch7.launchType = SearchHttpTaskAsyncHandler.GetEditorLaunchType();
                fullSearch7.SearchTime = base.SearchTime;
                fullSearch7.SearchCount = totalSearchResults.ToString();
                fullSearch7.ContextData = contextData;
                fullSearch7.ContextDataView = contextDataView;
                fullSearch7.CurrentPage = num;
                fullSearch7.Location = ((Context.ContentDatabase.GetItem(base.LocationFilter) != null) ? Context.ContentDatabase.GetItem(base.LocationFilter).Name : Translate.Text("current item"));
                FullSearch fullSearch8 = fullSearch7;
                string s4 = FormatCallbackString(fullSearch8);
                context.Response.Write(s4);
                if (BucketConfigurationSettings.EnableBucketDebug || Sitecore.Buckets.Util.Constants.EnableTemporaryBucketDebug)
                {
                    SearchLog.Log.Info("Search Took : " + base.Stopwatch.ElapsedMilliseconds + "ms");
                }
            }
        }

        private string FormatCallbackString(FullSearch fullSearch)
        {
            return base.Callback + "(" + JsonConvert.SerializeObject(fullSearch) + ")";
        }

        private List<UISearchResult> ProcessCachedItems(IEnumerable<UISearchResult> items, SitecoreIndexableItem startLocationItem, List<TemplateFieldItem> showFieldsQuick, List<UISearchResult> enumerableCollextion)
        {
            if (items == null)
            {
                return enumerableCollextion;
            }
            if (Context.ContentDatabase == null)
            {
                return enumerableCollextion;
            }
            ISearchIndex index;
            try
            {
                if (ContentSearchManager.GetContextIndexName((SitecoreIndexableItem)Context.ContentDatabase.GetItem(ItemIDs.TemplateRoot)) == null)
                {
                    return FillSearchResults(showFieldsQuick, enumerableCollextion);
                }
                index = ContentSearchManager.GetIndex((SitecoreIndexableItem)Context.ContentDatabase.GetItem(ItemIDs.TemplateRoot));
            }
            catch (IndexNotFoundException)
            {
                SearchLog.Log.Warn("No index found for " + ItemIDs.TemplateRoot);
                return enumerableCollextion;
            }
            using (IProviderSearchContext searchContext = index.CreateSearchContext())
            {
                IEnumerable<Tuple<string, string, string>> enumerable = ProcessCachedDisplayedSearch(startLocationItem, searchContext);
                ItemCache itemCache = CacheManager.GetItemCache(Context.ContentDatabase);
                foreach (Tuple<string, string, string> item2 in enumerable)
                {
                    Sitecore.Globalization.Language.TryParse(item2.Item2, out Language result);
                    Item item = itemCache.GetItem(new ID(item2.Item1), result, new Sitecore.Data.Version(item2.Item3));
                    if (item == null)
                    {
                        item = Context.ContentDatabase.GetItem(new ID(item2.Item1), result, new Sitecore.Data.Version(item2.Item3));
                        if (item != null)
                        {
                            CacheManager.GetItemCache(Context.ContentDatabase).AddItem(item.ID, result, item.Version, item);
                        }
                    }
                    if (item != null && !showFieldsQuick.Contains(FieldTypeManager.GetTemplateFieldItem(new Field(item.ID, item))))
                    {
                        showFieldsQuick.Add(FieldTypeManager.GetTemplateFieldItem(new Field(item.ID, item)));
                    }
                }
            }
            return FillSearchResults(showFieldsQuick, enumerableCollextion);
        }

        private List<UISearchResult> FillSearchResults(List<TemplateFieldItem> showFieldsQuick, List<UISearchResult> enumerableCollextion)
        {
            return FillItemPipeline.Run(new FillItemArgs(showFieldsQuick, enumerableCollextion, base.Language));
        }

        private static IEnumerable<Tuple<string, string, string>> ProcessCachedDisplayedSearch(SitecoreIndexableItem startLocationItem, IProviderSearchContext searchContext)
        {
            string text = "IsDisplayedInSearchResults" + "[" + Context.ContentDatabase.Name + "]";
            ICache cache = (ICache)CacheHashTable[text];
            IEnumerable<Tuple<string, string, string>> enumerable = (cache != null) ? (cache.GetValue("cachedIsDisplayedSearch") as IEnumerable<Tuple<string, string, string>>) : null;
            if (enumerable == null)
            {
                CultureInfo culture = (startLocationItem != null) ? startLocationItem.Culture : new CultureInfo(Settings.DefaultLanguage);
                enumerable = (from templateField in searchContext.GetQueryable<SitecoreUISearchResultItem>(new CultureExecutionContext(culture))
                              where templateField["Is Displayed in Search Results".ToLowerInvariant()] == "1"
                              select templateField).ToList().ConvertAll((SitecoreUISearchResultItem d) => new Tuple<string, string, string>(d.GetItem().ID.ToString(), d.Language, d.Version));
                if (CacheHashTable[text] == null)
                {
                    lock (CacheHashTable.SyncRoot)
                    {
                        if (CacheHashTable[text] == null)
                        {
                            List<ID> list = new List<ID>();
                            list.Add(new ID(Sitecore.Buckets.Util.Constants.IsDisplayedInSearchResults));
                            cache = new DisplayedInSearchResultsCache(text, list);
                            cacheHashtable[text] = cache;
                        }
                    }
                }
                cache.Add("cachedIsDisplayedSearch", enumerable);
            }
            return enumerable;
        }
        #endregion

        #region Modified code
        public override async Task ProcessRequestAsync(HttpContext context)
        {
            if (context.Request.HttpMethod != "POST" || !ContentSearchManager.Locator.GetInstance<Sitecore.ContentSearch.Utilities.IContentSearchConfigurationSettings>().ItemBucketsEnabled())
            {
                return;
            }

            //Fixing bug #214241
            if (BucketConfigurationSettings.EnableBucketDebug)
            {
                NameValueCollection queryString = context.Request.QueryString;
                Uri urlReferrer = context.Request.UrlReferrer;
                string newLine = Environment.NewLine;
                string text = ReadPostedData(context.Request.InputStream);
                SearchLog.Log.Info($"Search Handler Parameters:{newLine}\tQuery String: {queryString}{newLine}\tReferrer: {urlReferrer} \r\n Data:{text}");
            }

            context.Response.ContentType = "application/json";
            context.Response.ContentEncoding = Encoding.UTF8;
            base.Stopwatch = new Stopwatch();
            base.ItemsPerPage = BucketConfigurationSettings.DefaultNumberOfResultsPerPage;
            ExtractSearchQuery(context.Request.QueryString);
            ExtractSearchQuery(context.Request.Form);

            //Fixing bug #214241
            ExtractSearchQueryFromPostedData(context.Request.InputStream);

            CheckSecurity();
            if (base.AbortSearch)
            {
                return;
            }
            bool @bool = MainUtil.GetBool(Sitecore.ContentSearch.Utilities.SearchHelper.GetDebug(base.SearchQuery), defaultValue: false);
            if (@bool)
            {
                base.SearchQuery.RemoveAll((Sitecore.ContentSearch.Utilities.SearchStringModel x) => x.Type == "debug");
                if (!BucketConfigurationSettings.EnableBucketDebug)
                {
                    Sitecore.Buckets.Util.Constants.EnableTemporaryBucketDebug = true;
                }
            }
            try
            {
                PerformSearch(context);
            }
            finally
            {
                if (@bool)
                {
                    Sitecore.Buckets.Util.Constants.EnableTemporaryBucketDebug = false;
                }
            }
        }

        #endregion

        #region Added code
        private void ExtractSearchQueryFromPostedData(Stream requestInputStream)
        {
            string text = ReadPostedData(requestInputStream);
            if (!string.IsNullOrEmpty(text))
            {
                NameValueCollection parameters = HttpUtility.ParseQueryString(text);
                ExtractSearchQuery(parameters);
            }
        }

        private static string ReadPostedData(Stream requestInputStream)
        {
            if (requestInputStream == null || requestInputStream.Length < 1)
            {
                return null;
            }
            byte[] array = new byte[requestInputStream.Length];
            requestInputStream.Read(array, 0, array.Length);
            requestInputStream.Position = 0L;
            return Encoding.ASCII.GetString(array);
        }
        #endregion
    }
}