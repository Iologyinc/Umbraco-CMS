using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.XPath;
using Examine;
using Examine.LuceneEngine.SearchCriteria;
using Examine.Providers;
using Lucene.Net.Documents;
using Lucene.Net.Store;
using Umbraco.Core;
using Umbraco.Core.Configuration;
using Umbraco.Core.Dynamics;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Models.PublishedContent;
using Umbraco.Core.Xml;
using Umbraco.Web.Models;
using UmbracoExamine;
using umbraco;
using Umbraco.Core.Cache;
using Umbraco.Core.Sync;
using Umbraco.Web.Cache;

namespace Umbraco.Web.PublishedCache.XmlPublishedCache
{
    /// <summary>
    /// An IPublishedMediaStore that first checks for the media in Examine, and then reverts to the database
    /// </summary>
    /// <remarks>
    /// NOTE: In the future if we want to properly cache all media this class can be extended or replaced when these classes/interfaces are exposed publicly.
    /// </remarks>
    internal class PublishedMediaCache : IPublishedMediaCache
    {
        public PublishedMediaCache(ApplicationContext applicationContext)
        {
            if (applicationContext == null) throw new ArgumentNullException("applicationContext");
            _applicationContext = applicationContext;
        }

        /// <summary>
        /// Generally used for unit testing to use an explicit examine searcher
        /// </summary>
        /// <param name="applicationContext"></param>
        /// <param name="searchProvider"></param>
        /// <param name="indexProvider"></param>
        internal PublishedMediaCache(ApplicationContext applicationContext, BaseSearchProvider searchProvider, BaseIndexProvider indexProvider)
        {
            if (applicationContext == null) throw new ArgumentNullException("applicationContext");
            if (searchProvider == null) throw new ArgumentNullException("searchProvider");
            if (indexProvider == null) throw new ArgumentNullException("indexProvider");

            _applicationContext = applicationContext;
            _searchProvider = searchProvider;
            _indexProvider = indexProvider;
        }

        static PublishedMediaCache()
        {
            InitializeCacheConfig();
        }

        private readonly ApplicationContext _applicationContext;
        private readonly BaseSearchProvider _searchProvider;
        private readonly BaseIndexProvider _indexProvider;

        public virtual IPublishedContent GetById(UmbracoContext umbracoContext, bool preview, int nodeId)
        {
            return GetUmbracoMedia(nodeId);
        }

        public virtual IPublishedContent GetById(UmbracoContext umbracoContext, bool preview, Guid nodeKey)
        {
            // TODO optimize with Examine?
            var mapAttempt = ApplicationContext.Current.Services.IdkMap.GetIdForKey(nodeKey, UmbracoObjectTypes.Media);
            return mapAttempt ? GetById(umbracoContext, preview, mapAttempt.Result) : null;
        }

        public virtual IEnumerable<IPublishedContent> GetAtRoot(UmbracoContext umbracoContext, bool preview)
        {
            var searchProvider = GetSearchProviderSafe();

            if (searchProvider != null)
            {
                try
                {
                    // first check in Examine for the cache values
                    // +(+parentID:-1) +__IndexType:media

                    var criteria = searchProvider.CreateSearchCriteria("media");
                    var filter = criteria.ParentId(-1).Not().Field(UmbracoContentIndexer.IndexPathFieldName, "-1,-21,".MultipleCharacterWildcard());

                    var result = searchProvider.Search(filter.Compile());
                    if (result != null)
                        return result.Select(x => CreateFromCacheValues(ConvertFromSearchResult(x)));
                }
                catch (Exception ex)
                {
                    if (ex is FileNotFoundException)
                    {
                        //Currently examine is throwing FileNotFound exceptions when we have a loadbalanced filestore and a node is published in umbraco
                        //See this thread: http://examine.cdodeplex.com/discussions/264341
                        //Catch the exception here for the time being, and just fallback to GetMedia
                        //TODO: Need to fix examine in LB scenarios!
                        LogHelper.Error<PublishedMediaCache>("Could not load data from Examine index for media", ex);
                    }
                    else if (ex is AlreadyClosedException)
                    {
                        //If the app domain is shutting down and the site is under heavy load the index reader will be closed and it really cannot
                        //be re-opened since the app domain is shutting down. In this case we have no option but to try to load the data from the db.
                        LogHelper.Error<PublishedMediaCache>("Could not load data from Examine index for media, the app domain is most likely in a shutdown state", ex);
                    }
                    else throw;
                }
            }

            //something went wrong, fetch from the db

            var rootMedia = _applicationContext.Services.MediaService.GetRootMedia();
            return rootMedia.Select(m => CreateFromCacheValues(ConvertFromIMedia(m)));
        }

        public virtual IPublishedContent GetSingleByXPath(UmbracoContext umbracoContext, bool preview, string xpath, XPathVariable[] vars)
        {
            throw new NotImplementedException("PublishedMediaCache does not support XPath.");
        }

        public virtual IPublishedContent GetSingleByXPath(UmbracoContext umbracoContext, bool preview, XPathExpression xpath, XPathVariable[] vars)
        {
            throw new NotImplementedException("PublishedMediaCache does not support XPath.");
        }

        public virtual IEnumerable<IPublishedContent> GetByXPath(UmbracoContext umbracoContext, bool preview, string xpath, XPathVariable[] vars)
        {
            throw new NotImplementedException("PublishedMediaCache does not support XPath.");
        }

        public virtual IEnumerable<IPublishedContent> GetByXPath(UmbracoContext umbracoContext, bool preview, XPathExpression xpath, XPathVariable[] vars)
        {
            throw new NotImplementedException("PublishedMediaCache does not support XPath.");
        }

        public virtual XPathNavigator GetXPathNavigator(UmbracoContext umbracoContext, bool preview)
        {
            throw new NotImplementedException("PublishedMediaCache does not support XPath.");
        }

        public bool XPathNavigatorIsNavigable { get { return false; } }

        public virtual bool HasContent(UmbracoContext context, bool preview) { throw new NotImplementedException(); }

        private ExamineManager GetExamineManagerSafe()
        {
            try
            {
                return ExamineManager.Instance;
            }
            catch (TypeInitializationException)
            {
                return null;
            }
        }

        private BaseIndexProvider GetIndexProviderSafe()
        {
            if (_indexProvider != null)
                return _indexProvider;

            var eMgr = GetExamineManagerSafe();
            if (eMgr != null)
            {
                try
                {
                    //by default use the InternalSearcher
                    var indexer = eMgr.IndexProviderCollection[Constants.Examine.InternalIndexer];
                    if (indexer.IndexerData.IncludeNodeTypes.Any() || indexer.IndexerData.ExcludeNodeTypes.Any())
                    {
                        LogHelper.Warn<PublishedMediaCache>("The InternalIndexer for examine is configured incorrectly, it should not list any include/exclude node types or field names, it should simply be configured as: " + "<IndexSet SetName=\"InternalIndexSet\" IndexPath=\"~/App_Data/TEMP/ExamineIndexes/Internal/\" />");
                    }
                    return indexer;
                }
                catch (Exception ex)
                {
                    LogHelper.Error<PublishedMediaCache>("Could not retrieve the InternalIndexer", ex);
                    //something didn't work, continue returning null.
                }
            }
            return null;
        }

        private BaseSearchProvider GetSearchProviderSafe()
        {
            if (_searchProvider != null)
                return _searchProvider;

            var eMgr = GetExamineManagerSafe();
            if (eMgr != null)
            {
                try
                {
                    //by default use the InternalSearcher
                    return eMgr.SearchProviderCollection[Constants.Examine.InternalSearcher];
                }
                catch (FileNotFoundException)
                {
                    //Currently examine is throwing FileNotFound exceptions when we have a loadbalanced filestore and a node is published in umbraco
                    //See this thread: http://examine.cdodeplex.com/discussions/264341
                    //Catch the exception here for the time being, and just fallback to GetMedia
                    //TODO: Need to fix examine in LB scenarios!
                }
                catch (NullReferenceException)
                {
                    //This will occur when the search provider cannot be initialized. In newer examine versions the initialization is lazy and therefore
                    // the manager will return the singleton without throwing initialization errors, however if examine isn't configured correctly a null
                    // reference error will occur because the examine settings are null.
                }
                catch (AlreadyClosedException)
                {
                    //If the app domain is shutting down and the site is under heavy load the index reader will be closed and it really cannot
                    //be re-opened since the app domain is shutting down. In this case we have no option but to try to load the data from the db.
                }
            }
            return null;
        }

        private IPublishedContent GetUmbracoMedia(int id)
        {
            // this recreates an IPublishedContent and model each time
            // it is called, but at least it should NOT hit the database
            // nor Lucene each time, relying on the memory cache instead

            if (id <= 0) return null; // fail fast

            var cacheValues = GetCacheValues(id, GetUmbracoMediaCacheValues);

            return cacheValues == null ? null : CreateFromCacheValues(cacheValues);
        }

        private CacheValues GetUmbracoMediaCacheValues(int id)
        {
            var searchProvider = GetSearchProviderSafe();

            if (searchProvider != null)
            {
                try
                {
                    // first check in Examine as this is WAY faster
                    //
                    // the filter will create a query like this:
                    // +(+__NodeId:3113 -__Path:-1,-21,*) +__IndexType:media
                    //
                    // note that since the use of the wildcard, it automatically escapes it in Lucene.

                    var criteria = searchProvider.CreateSearchCriteria("media");
                    var filter = criteria.Id(id).Not().Field(UmbracoContentIndexer.IndexPathFieldName, "-1,-21,".MultipleCharacterWildcard());

                    var result = searchProvider.Search(filter.Compile()).FirstOrDefault();
                    if (result != null) return ConvertFromSearchResult(result);
                }
                catch (Exception ex)
                {
                    if (ex is FileNotFoundException)
                    {
                        //Currently examine is throwing FileNotFound exceptions when we have a loadbalanced filestore and a node is published in umbraco
                        //See this thread: http://examine.cdodeplex.com/discussions/264341
                        //Catch the exception here for the time being, and just fallback to GetMedia
                        //TODO: Need to fix examine in LB scenarios!
                        LogHelper.Error<PublishedMediaCache>("Could not load data from Examine index for media", ex);
                    }
                    else if (ex is AlreadyClosedException)
                    {
                        //If the app domain is shutting down and the site is under heavy load the index reader will be closed and it really cannot
                        //be re-opened since the app domain is shutting down. In this case we have no option but to try to load the data from the db.
                        LogHelper.Error<PublishedMediaCache>("Could not load data from Examine index for media, the app domain is most likely in a shutdown state", ex);
                    }
                    else throw;
                }
            }

            // don't log a warning here, as it can flood the log in case of eg a media picker referencing a media
            // that has been deleted, hence is not in the Examine index anymore (for a good reason). try to get
            // the media from the service, first

            var media = ApplicationContext.Current.Services.MediaService.GetById(id);
            if (media == null || media.Trashed) return null; // not found, ok

            // so, the media was not found in Examine's index *yet* it exists, which probably indicates that
            // the index is corrupted. Or not up-to-date. Log a warning, but only once, and only if seeing the
            // error more that a number of times.

            var miss = Interlocked.CompareExchange(ref _examineIndexMiss, 0, 0); // volatile read
            if (miss < ExamineIndexMissMax && Interlocked.Increment(ref _examineIndexMiss) == ExamineIndexMissMax)
                LogHelper.Warn<PublishedMediaCache>("Failed ({0} times) to retrieve medias from Examine index and had to load"
                    + " them from DB. This may indicate that the Examine index is corrupted.",
                    () => ExamineIndexMissMax);

            return ConvertFromIMedia(media);
        }

        private const int ExamineIndexMissMax = 10;
        private int _examineIndexMiss;

        internal CacheValues ConvertFromXPathNodeIterator(XPathNodeIterator media, int id)
        {
            if (media != null && media.Current != null)
            {
                return media.Current.Name.InvariantEquals("error")
                    ? null
                    : ConvertFromXPathNavigator(media.Current);
            }

            LogHelper.Warn<PublishedMediaCache>(
                "Could not retrieve media {0} from Examine index or from legacy library.GetMedia method",
                () => id);

            return null;
        }

        internal CacheValues ConvertFromSearchResult(SearchResult searchResult)
        {
            //NOTE: Some fields will not be included if the config section for the internal index has been
            //mucked around with. It should index everything and so the index definition should simply be:
            // <IndexSet SetName="InternalIndexSet" IndexPath="~/App_Data/TEMP/ExamineIndexes/Internal/" />


            var values = new Dictionary<string, string>(searchResult.Fields);
            //we need to ensure some fields exist, because of the above issue
            if (!new[] { "template", "templateId" }.Any(values.ContainsKey))
                values.Add("template", 0.ToString());
            if (!new[] { "sortOrder" }.Any(values.ContainsKey))
                values.Add("sortOrder", 0.ToString());
            if (!new[] { "urlName" }.Any(values.ContainsKey))
                values.Add("urlName", "");
            if (!new[] { "nodeType" }.Any(values.ContainsKey))
                values.Add("nodeType", 0.ToString());
            if (!new[] { "creatorName" }.Any(values.ContainsKey))
                values.Add("creatorName", "");
            if (!new[] { "writerID" }.Any(values.ContainsKey))
                values.Add("writerID", 0.ToString());
            if (!new[] { "creatorID" }.Any(values.ContainsKey))
                values.Add("creatorID", 0.ToString());
            if (!new[] { "createDate" }.Any(values.ContainsKey))
                values.Add("createDate", default(DateTime).ToString("yyyy-MM-dd HH:mm:ss"));
            if (!new[] { "level" }.Any(values.ContainsKey))
            {
                values.Add("level", values["__Path"].Split(',').Length.ToString());
            }

            // because, migration
            if (values.ContainsKey("key") == false)
                values["key"] = Guid.Empty.ToString();

            return new CacheValues
            {
                Values = values,
                FromExamine = true
            };

            //var content = new DictionaryPublishedContent(values,
            //                                      d => d.ParentId != -1 //parent should be null if -1
            //                                               ? GetUmbracoMedia(d.ParentId)
            //                                               : null,
            //                                      //callback to return the children of the current node
            //                                      d => GetChildrenMedia(d.Id),
            //                                      GetProperty,
            //                                      true);
            //return content.CreateModel();
        }

        internal CacheValues ConvertFromXPathNavigator(XPathNavigator xpath, bool forceNav = false)
        {
            if (xpath == null) throw new ArgumentNullException("xpath");

            var values = new Dictionary<string, string> { { "nodeName", xpath.GetAttribute("nodeName", "") } };
            if (!UmbracoConfig.For.UmbracoSettings().Content.UseLegacyXmlSchema)
            {
                values["nodeTypeAlias"] = xpath.Name;
            }

            var result = xpath.SelectChildren(XPathNodeType.Element);
            //add the attributes e.g. id, parentId etc
            if (result.Current != null && result.Current.HasAttributes)
            {
                if (result.Current.MoveToFirstAttribute())
                {
                    //checking for duplicate keys because of the 'nodeTypeAlias' might already be added above.
                    if (!values.ContainsKey(result.Current.Name))
                    {
                        values[result.Current.Name] = result.Current.Value;
                    }
                    while (result.Current.MoveToNextAttribute())
                    {
                        if (!values.ContainsKey(result.Current.Name))
                        {
                            values[result.Current.Name] = result.Current.Value;
                        }
                    }
                    result.Current.MoveToParent();
                }
            }
            // because, migration
            if (values.ContainsKey("key") == false)
                values["key"] = Guid.Empty.ToString();
            //add the user props
            while (result.MoveNext())
            {
                if (result.Current != null && !result.Current.HasAttributes)
                {
                    string value = result.Current.Value;
                    if (string.IsNullOrEmpty(value))
                    {
                        if (result.Current.HasAttributes || result.Current.SelectChildren(XPathNodeType.Element).Count > 0)
                        {
                            value = result.Current.OuterXml;
                        }
                    }
                    values[result.Current.Name] = value;
                }
            }

            return new CacheValues
            {
                Values = values,
                XPath = forceNav ? xpath : null // outside of tests we do NOT want to cache the navigator!
            };

            //var content = new DictionaryPublishedContent(values,
            //    d => d.ParentId != -1 //parent should be null if -1
            //        ? GetUmbracoMedia(d.ParentId)
            //        : null,
            //    //callback to return the children of the current node based on the xml structure already found
            //    d => GetChildrenMedia(d.Id, xpath),
            //    GetProperty,
            //    false);
            //return content.CreateModel();
        }

        internal CacheValues ConvertFromIMedia(IMedia media)
        {
            var values = new Dictionary<string, string>();

            var creator = _applicationContext.Services.UserService.GetProfileById(media.CreatorId);
            var creatorName = creator == null ? "" : creator.Name;

            values["id"] = media.Id.ToString();
            values["key"] = media.Key.ToString();
            values["parentID"] = media.ParentId.ToString();
            values["level"] = media.Level.ToString();
            values["creatorID"] = media.CreatorId.ToString();
            values["creatorName"] = creatorName;
            values["writerID"] = media.CreatorId.ToString();
            values["writerName"] = creatorName;
            values["template"] = "0";
            values["urlName"] = "";
            values["sortOrder"] = media.SortOrder.ToString();
            values["createDate"] = media.CreateDate.ToString("yyyy-MM-dd HH:mm:ss");
            values["updateDate"] = media.UpdateDate.ToString("yyyy-MM-dd HH:mm:ss");
            values["nodeName"] = media.Name;
            values["path"] = media.Path;
            values["nodeType"] = media.ContentType.Id.ToString();
            values["nodeTypeAlias"] = media.ContentType.Alias;

            // add the user props
            foreach (var prop in media.Properties)
                values[prop.Alias] = prop.Value == null ? null : prop.Value.ToString();

            return new CacheValues
            {
                Values = values
            };
        }

        /// <summary>
        /// We will need to first check if the document was loaded by Examine, if so we'll need to check if this property exists
        /// in the results, if it does not, then we'll have to revert to looking up in the db.
        /// </summary>
        /// <param name="dd"> </param>
        /// <param name="alias"></param>
        /// <returns></returns>
        private IPublishedProperty GetProperty(DictionaryPublishedContent dd, string alias)
        {
            //lets check if the alias does not exist on the document.
            //NOTE: Examine will not index empty values and we do not output empty XML Elements to the cache - either of these situations
            // would mean that the property is missing from the collection whether we are getting the value from Examine or from the library media cache.
            if (dd.Properties.All(x => x.PropertyTypeAlias.InvariantEquals(alias) == false))
            {
                return null;
            }

            if (dd.LoadedFromExamine)
            {
                //We are going to check for a special field however, that is because in some cases we store a 'Raw'
                //value in the index such as for xml/html.
                var rawValue = dd.Properties.FirstOrDefault(x => x.PropertyTypeAlias.InvariantEquals(UmbracoContentIndexer.RawFieldPrefix + alias));
                return rawValue
                       ?? dd.Properties.FirstOrDefault(x => x.PropertyTypeAlias.InvariantEquals(alias));
            }

            //if its not loaded from examine, then just return the property
            return dd.Properties.FirstOrDefault(x => x.PropertyTypeAlias.InvariantEquals(alias));
        }

        /// <summary>
        /// A Helper methods to return the children for media whther it is based on examine or xml
        /// </summary>
        /// <param name="parentId"></param>
        /// <param name="xpath"></param>
        /// <returns></returns>
        private IEnumerable<IPublishedContent> GetChildrenMedia(int parentId, XPathNavigator xpath = null)
        {

            //if there is no navigator, try examine first, then re-look it up
            if (xpath == null)
            {
                var searchProvider = GetSearchProviderSafe();

                if (searchProvider != null)
                {
                    try
                    {
                        //first check in Examine as this is WAY faster
                        var criteria = searchProvider.CreateSearchCriteria("media");

                        var filter = criteria.ParentId(parentId).Not().Field(UmbracoContentIndexer.IndexPathFieldName, "-1,-21,".MultipleCharacterWildcard());
                        //the above filter will create a query like this, NOTE: That since the use of the wildcard, it automatically escapes it in Lucene.
                        //+(+parentId:3113 -__Path:-1,-21,*) +__IndexType:media

                        ISearchResults results;

                        //we want to check if the indexer for this searcher has "sortOrder" flagged as sortable.
                        //if so, we'll use Lucene to do the sorting, if not we'll have to manually sort it (slower).
                        var indexer = GetIndexProviderSafe();
                        var useLuceneSort = indexer != null && indexer.IndexerData.StandardFields.Any(x => x.Name.InvariantEquals("sortOrder") && x.EnableSorting);
                        if (useLuceneSort)
                        {
                            //we have a sortOrder field declared to be sorted, so we'll use Examine
                            results = searchProvider.Search(
                                filter.And().OrderBy(new SortableField("sortOrder", SortType.Int)).Compile());
                        }
                        else
                        {
                            results = searchProvider.Search(filter.Compile());
                        }

                        if (results.Any())
                        {
                            // var medias = results.Select(ConvertFromSearchResult);
                            var medias = results.Select(x =>
                            {
                                int nid;
                                if (int.TryParse(x["__NodeId"], out nid) == false && int.TryParse(x["NodeId"], out nid) == false)
                                    throw new Exception("Failed to extract NodeId from search result.");
                                var cacheValues = GetCacheValues(nid, id => ConvertFromSearchResult(x));
                                return CreateFromCacheValues(cacheValues);
                            });

                            return useLuceneSort ? medias : medias.OrderBy(x => x.SortOrder);
                        }
                        else
                        {
                            //if there's no result then return null. Previously we defaulted back to library.GetMedia below
                            //but this will always get called for when we are getting descendents since many items won't have
                            //children and then we are hitting the database again!
                            //So instead we're going to rely on Examine to have the correct results like it should.
                            return Enumerable.Empty<IPublishedContent>();
                        }
                    }
                    catch (FileNotFoundException)
                    {
                        //Currently examine is throwing FileNotFound exceptions when we have a loadbalanced filestore and a node is published in umbraco
                        //See this thread: http://examine.cdodeplex.com/discussions/264341
                        //Catch the exception here for the time being, and just fallback to GetMedia
                    }
                }

                //falling back to get media

                var media = library.GetMedia(parentId, true);
                if (media != null && media.Current != null)
                {
                    xpath = media.Current;
                }
                else
                {
                    return Enumerable.Empty<IPublishedContent>();
                }
            }

            var mediaList = new List<IPublishedContent>();

            // this is so bad, really
            var item = xpath.Select("//*[@id='" + parentId + "']");
            if (item.Current == null)
                return Enumerable.Empty<IPublishedContent>();
            var items = item.Current.SelectChildren(XPathNodeType.Element);

            // and this does not work, because... meh
            //var q = "//* [@id='" + parentId + "']/* [@id]";
            //var items = xpath.Select(q);

            foreach (XPathNavigator itemm in items)
            {
                int id;
                if (int.TryParse(itemm.GetAttribute("id", ""), out id) == false)
                    continue; // wtf?
                var captured = itemm;
                var cacheValues = GetCacheValues(id, idd => ConvertFromXPathNavigator(captured));
                mediaList.Add(CreateFromCacheValues(cacheValues));
            }

            ////The xpath might be the whole xpath including the current ones ancestors so we need to select the current node
            //var item = xpath.Select("//*[@id='" + parentId + "']");
            //if (item.Current == null)
            //{
            //    return Enumerable.Empty<IPublishedContent>();
            //}
            //var children = item.Current.SelectChildren(XPathNodeType.Element);

            //foreach(XPathNavigator x in children)
            //{
            //    //NOTE: I'm not sure why this is here, it is from legacy code of ExamineBackedMedia, but
            //    // will leave it here as it must have done something!
            //    if (x.Name != "contents")
            //    {
            //        //make sure it's actually a node, not a property
            //        if (!string.IsNullOrEmpty(x.GetAttribute("path", "")) &&
            //            !string.IsNullOrEmpty(x.GetAttribute("id", "")))
            //        {
            //            mediaList.Add(ConvertFromXPathNavigator(x));
            //        }
            //    }
            //}

            return mediaList;
        }

        /// <summary>
        /// An IPublishedContent that is represented all by a dictionary.
        /// </summary>
        /// <remarks>
        /// This is a helper class and definitely not intended for public use, it expects that all of the values required
        /// to create an IPublishedContent exist in the dictionary by specific aliases.
        /// </remarks>
        internal class DictionaryPublishedContent : PublishedContentWithKeyBase
        {
            // note: I'm not sure this class fully complies with IPublishedContent rules especially
            // I'm not sure that _properties contains all properties including those without a value,
            // neither that GetProperty will return a property without a value vs. null... @zpqrtbnk

            // List of properties that will appear in the XML and do not match
            // anything in the ContentType, so they must be ignored.
            private static readonly string[] IgnoredKeys = { "version", "isDoc" };

            public DictionaryPublishedContent(
                IDictionary<string, string> valueDictionary,
                Func<int, IPublishedContent> getParent,
                Func<int, XPathNavigator, IEnumerable<IPublishedContent>> getChildren,
                Func<DictionaryPublishedContent, string, IPublishedProperty> getProperty,
                XPathNavigator nav,
                bool fromExamine)
            {
                if (valueDictionary == null) throw new ArgumentNullException("valueDictionary");
                if (getParent == null) throw new ArgumentNullException("getParent");
                if (getProperty == null) throw new ArgumentNullException("getProperty");

                _getParent = new Lazy<IPublishedContent>(() => getParent(ParentId));
                _getChildren = new Lazy<IEnumerable<IPublishedContent>>(() => getChildren(Id, nav));
                _getProperty = getProperty;

                LoadedFromExamine = fromExamine;

                ValidateAndSetProperty(valueDictionary, val => _id = int.Parse(val), "id", "nodeId", "__NodeId"); //should validate the int!
                ValidateAndSetProperty(valueDictionary, val => _key = Guid.Parse(val), "key");
                // wtf are we dealing with templates for medias?!
                ValidateAndSetProperty(valueDictionary, val => _templateId = int.Parse(val), "template", "templateId");
                ValidateAndSetProperty(valueDictionary, val => _sortOrder = int.Parse(val), "sortOrder");
                ValidateAndSetProperty(valueDictionary, val => _name = val, "nodeName", "__nodeName");
                ValidateAndSetProperty(valueDictionary, val => _urlName = val, "urlName");
                ValidateAndSetProperty(valueDictionary, val => _documentTypeAlias = val, "nodeTypeAlias", UmbracoContentIndexer.NodeTypeAliasFieldName);
                ValidateAndSetProperty(valueDictionary, val => _documentTypeId = int.Parse(val), "nodeType");
                ValidateAndSetProperty(valueDictionary, val => _writerName = val, "writerName");
                ValidateAndSetProperty(valueDictionary, val => _creatorName = val, "creatorName", "writerName"); //this is a bit of a hack fix for: U4-1132
                ValidateAndSetProperty(valueDictionary, val => _writerId = int.Parse(val), "writerID");
                ValidateAndSetProperty(valueDictionary, val => _creatorId = int.Parse(val), "creatorID", "writerID"); //this is a bit of a hack fix for: U4-1132
                ValidateAndSetProperty(valueDictionary, val => _path = val, "path", "__Path");
                ValidateAndSetProperty(valueDictionary, val => _createDate = ParseDateTimeValue(val), "createDate");
                ValidateAndSetProperty(valueDictionary, val => _updateDate = ParseDateTimeValue(val), "updateDate");
                ValidateAndSetProperty(valueDictionary, val => _level = int.Parse(val), "level");
                ValidateAndSetProperty(valueDictionary, val =>
                {
                    int pId;
                    ParentId = -1;
                    if (int.TryParse(val, out pId))
                    {
                        ParentId = pId;
                    }
                }, "parentID");

                _contentType = PublishedContentType.Get(PublishedItemType.Media, _documentTypeAlias);
                _properties = new Collection<IPublishedProperty>();

                //handle content type properties
                //make sure we create them even if there's no value
                foreach (var propertyType in _contentType.PropertyTypes)
                {
                    var alias = propertyType.PropertyTypeAlias;
                    _keysAdded.Add(alias);
                    string value;
                    const bool isPreviewing = false; // false :: never preview a media
                    var property = valueDictionary.TryGetValue(alias, out value) == false || value == null
                        ? new XmlPublishedProperty(propertyType, isPreviewing)
                        : new XmlPublishedProperty(propertyType, isPreviewing, value);
                    _properties.Add(property);
                }

                //loop through remaining values that haven't been applied
                foreach (var i in valueDictionary.Where(x =>
                    _keysAdded.Contains(x.Key) == false // not already processed
                    && IgnoredKeys.Contains(x.Key) == false)) // not ignorable
                {
                    if (i.Key.InvariantStartsWith("__"))
                    {
                        // no type for that one, dunno how to convert
                        IPublishedProperty property = new PropertyResult(i.Key, i.Value, PropertyResultType.CustomProperty);
                        _properties.Add(property);
                    }
                    else
                    {
                        // this is a property that does not correspond to anything, ignore and log
                        LogHelper.Warn<PublishedMediaCache>("Dropping property \"" + i.Key + "\" because it does not belong to the content type.");
                    }
                }
            }

            private DateTime ParseDateTimeValue(string val)
            {
                if (LoadedFromExamine)
                {
                    try
                    {
                        //we might need to parse the date time using Lucene converters
                        return DateTools.StringToDate(val);
                    }
                    catch (FormatException)
                    {
                        //swallow exception, its not formatted correctly so revert to just trying to parse
                    }
                }

                return DateTime.Parse(val);
            }

            /// <summary>
            /// Flag to get/set if this was laoded from examine cache
            /// </summary>
            internal bool LoadedFromExamine { get; private set; }

            //private readonly Func<DictionaryPublishedContent, IPublishedContent> _getParent;
            private readonly Lazy<IPublishedContent> _getParent;
            //private readonly Func<DictionaryPublishedContent, IEnumerable<IPublishedContent>> _getChildren;
            private readonly Lazy<IEnumerable<IPublishedContent>> _getChildren;
            private readonly Func<DictionaryPublishedContent, string, IPublishedProperty> _getProperty;

            /// <summary>
            /// Returns 'Media' as the item type
            /// </summary>
            public override PublishedItemType ItemType
            {
                get { return PublishedItemType.Media; }
            }

            public override IPublishedContent Parent
            {
                get { return _getParent.Value; }
            }

            public int ParentId { get; private set; }
            public override int Id
            {
                get { return _id; }
            }

            public override Guid Key { get { return _key; } }

            public override int TemplateId
            {
                get
                {
                    //TODO: should probably throw a not supported exception since media doesn't actually support this.
                    return _templateId;
                }
            }

            public override int SortOrder
            {
                get { return _sortOrder; }
            }

            public override string Name
            {
                get { return _name; }
            }

            public override string UrlName
            {
                get { return _urlName; }
            }

            public override string DocumentTypeAlias
            {
                get { return _documentTypeAlias; }
            }

            public override int DocumentTypeId
            {
                get { return _documentTypeId; }
            }

            public override string WriterName
            {
                get { return _writerName; }
            }

            public override string CreatorName
            {
                get { return _creatorName; }
            }

            public override int WriterId
            {
                get { return _writerId; }
            }

            public override int CreatorId
            {
                get { return _creatorId; }
            }

            public override string Path
            {
                get { return _path; }
            }

            public override DateTime CreateDate
            {
                get { return _createDate; }
            }

            public override DateTime UpdateDate
            {
                get { return _updateDate; }
            }

            public override Guid Version
            {
                get { return _version; }
            }

            public override int Level
            {
                get { return _level; }
            }

            public override bool IsDraft
            {
                get { return false; }
            }

            public override ICollection<IPublishedProperty> Properties
            {
                get { return _properties; }
            }

            public override IEnumerable<IPublishedContent> Children
            {
                get { return _getChildren.Value; }
            }

            public override IPublishedProperty GetProperty(string alias)
            {
                return _getProperty(this, alias);
            }

            public override PublishedContentType ContentType
            {
                get { return _contentType; }
            }

            // override to implement cache
            //   cache at context level, ie once for the whole request
            //   but cache is not shared by requests because we wouldn't know how to clear it
            public override IPublishedProperty GetProperty(string alias, bool recurse)
            {
                if (recurse == false) return GetProperty(alias);

                IPublishedProperty property;
                string key = null;
                var cache = UmbracoContextCache.Current;

                if (cache != null)
                {
                    key = string.Format("RECURSIVE_PROPERTY::{0}::{1}", Id, alias.ToLowerInvariant());
                    object o;
                    if (cache.TryGetValue(key, out o))
                    {
                        property = o as IPublishedProperty;
                        if (property == null)
                            throw new InvalidOperationException("Corrupted cache.");
                        return property;
                    }
                }

                // else get it for real, no cache
                property = base.GetProperty(alias, true);

                if (cache != null)
                    cache[key] = property;

                return property;
            }

            private readonly List<string> _keysAdded = new List<string>();
            private int _id;
            private Guid _key;
            private int _templateId;
            private int _sortOrder;
            private string _name;
            private string _urlName;
            private string _documentTypeAlias;
            private int _documentTypeId;
            private string _writerName;
            private string _creatorName;
            private int _writerId;
            private int _creatorId;
            private string _path;
            private DateTime _createDate;
            private DateTime _updateDate;
            private Guid _version;
            private int _level;
            private readonly ICollection<IPublishedProperty> _properties;
            private readonly PublishedContentType _contentType;

            private void ValidateAndSetProperty(IDictionary<string, string> valueDictionary, Action<string> setProperty, params string[] potentialKeys)
            {
                var key = potentialKeys.FirstOrDefault(x => valueDictionary.ContainsKey(x) && valueDictionary[x] != null);
                if (key == null)
                {
                    throw new FormatException("The valueDictionary is not formatted correctly and is missing any of the  '" + string.Join(",", potentialKeys) + "' elements");
                }

                setProperty(valueDictionary[key]);
                _keysAdded.Add(key);
            }
        }

        // REFACTORING

        // caching the basic atomic values - and the parent id
        // but NOT caching actual parent nor children and NOT even
        // the list of children ids - BUT caching the path

        internal class CacheValues
        {
            public IDictionary<string, string> Values { get; set; }
            public XPathNavigator XPath { get; set; }
            public bool FromExamine { get; set; }
        }

        public const string PublishedMediaCacheKey = "MediaCacheMeh.";
        private const int PublishedMediaCacheTimespanSeconds = 4 * 60; // 4 mins
        private static TimeSpan _publishedMediaCacheTimespan;
        private static bool _publishedMediaCacheEnabled;

        private static void InitializeCacheConfig()
        {
            var value = ConfigurationManager.AppSettings["Umbraco.PublishedMediaCache.Seconds"];
            int seconds;
            if (int.TryParse(value, out seconds) == false)
                seconds = PublishedMediaCacheTimespanSeconds;
            if (seconds > 0)
            {
                _publishedMediaCacheEnabled = true;
                _publishedMediaCacheTimespan = TimeSpan.FromSeconds(seconds);
            }
            else
            {
                _publishedMediaCacheEnabled = false;
            }
        }

        internal IPublishedContent CreateFromCacheValues(CacheValues cacheValues)
        {
            var content = new DictionaryPublishedContent(
                cacheValues.Values,
                parentId => parentId < 0 ? null : GetUmbracoMedia(parentId),
                GetChildrenMedia,
                GetProperty,
                cacheValues.XPath, // though, outside of tests, that should be null
                cacheValues.FromExamine
            );
            return content.CreateModel();
        }

        private static CacheValues GetCacheValues(int id, Func<int, CacheValues> func)
        {
            if (_publishedMediaCacheEnabled == false)
                return func(id);

            var cache = ApplicationContext.Current.ApplicationCache.RuntimeCache;
            var key = PublishedMediaCacheKey + id;
            return (CacheValues)cache.GetCacheItem(key, () => func(id), _publishedMediaCacheTimespan);
        }

        internal static void ClearCache(int id)
        {
            var cache = ApplicationContext.Current.ApplicationCache.RuntimeCache;
            var sid = id.ToString();
            var key = PublishedMediaCacheKey + sid;

            // we do clear a lot of things... but the cache refresher is somewhat
            // convoluted and it's hard to tell what to clear exactly ;-(

            // clear the parent - NOT (why?)
            //var exist = (CacheValues) cache.GetCacheItem(key);
            //if (exist != null)
            //    cache.ClearCacheItem(PublishedMediaCacheKey + GetValuesValue(exist.Values, "parentID"));

            // clear the item
            cache.ClearCacheItem(key);

            // clear all children - in case we moved and their path has changed
            var fid = "/" + sid + "/";
            cache.ClearCacheObjectTypes<CacheValues>((k, v) =>
                GetValuesValue(v.Values, "path", "__Path").Contains(fid));
        }

        private static string GetValuesValue(IDictionary<string, string> d, params string[] keys)
        {
            string value = null;
            var ignored = keys.Any(x => d.TryGetValue(x, out value));
            return value ?? "";
        }
    }
}
