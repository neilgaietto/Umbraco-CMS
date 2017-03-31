using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Web;
using System.Xml;
using Umbraco.Core.IO;
using Umbraco.Core.Logging;
using umbraco.BusinessLogic;
using umbraco.BusinessLogic.Actions;
using umbraco.cms.businesslogic.property;
using umbraco.cms.businesslogic.relation;
using umbraco.cms.helpers;
using umbraco.DataLayer;
using umbraco.IO;
using umbraco.interfaces;
using umbraco.cms.businesslogic.datatype.controls;
using System.IO;
using System.Diagnostics;
using Umbraco.Core;

namespace umbraco.cms.businesslogic.web
{
    /// <summary>
    /// Document represents a webpage,
    /// type (umbraco.cms.businesslogic.web.DocumentType)
    /// 
    /// Pubished Documents are exposed to the runtime/the public website in a cached xml document.
    /// </summary>
    public class Document : Content
    {
        #region Constructors

        /// <summary>
        /// Constructs a new document
        /// </summary>
        /// <param name="id">Id of the document</param>
        /// <param name="noSetup">true if the data shouldn't loaded from the db</param>
        public Document(Guid id, bool noSetup) : base(id, noSetup) { }

        /// <summary>
        /// Initializes a new instance of the Document class.
        /// You can set an optional flag noSetup, used for optimizing for loading nodes in the tree, 
        /// therefor only data needed by the tree is initialized.
        /// </summary>
        /// <param name="id">Id of the document</param>
        /// <param name="noSetup">true if the data shouldn't loaded from the db</param>
        public Document(int id, bool noSetup) : base(id, noSetup) { }

        /// <summary>
        /// Initializes a new instance of the Document class to a specific version, used for rolling back data from a previous version
        /// of the document.
        /// </summary>
        /// <param name="id">The id of the document</param>
        /// <param name="Version">The version of the document</param>
        public Document(int id, Guid Version)
            : base(id)
        {
            this.Version = Version;
        }

        /// <summary>
        /// Initializes a new instance of the Document class.
        /// </summary>
        /// <param name="id">The id of the document</param>
        public Document(int id) : base(id) { }

        /// <summary>
        /// Initialize the document
        /// </summary>
        /// <param name="id">The id of the document</param>
        public Document(Guid id) : base(id) { }

        /// <summary>
        /// Initializes a Document object with one SQL query instead of many
        /// </summary>
        /// <param name="optimizedMode"></param>
        /// <param name="id"></param>
        public Document(bool optimizedMode, int id)
            : base(id, optimizedMode)
        {
            this._optimizedMode = optimizedMode;

            if (optimizedMode)
            {

                using (IRecordsReader dr =
                        SqlHelper.ExecuteReader(string.Format(SqlOptimizedSingle.Trim(), "umbracoNode.id = @id", "cmsContentVersion.id desc"),
                            SqlHelper.CreateParameter("@nodeObjectType", Document._objectType),
                            SqlHelper.CreateParameter("@id", id)))
                {
                    if (dr.Read())
                    {
                        // Initialize node and basic document properties
                        int? masterContentType = null;
                        if (!dr.IsNull("masterContentType"))
                            masterContentType = dr.GetInt("masterContentType");
                        SetupDocumentForTree(dr.GetGuid("uniqueId")
                            , dr.GetShort("level")
                            , dr.GetInt("parentId")
                            , dr.GetInt("nodeUser")
                            , dr.GetInt("documentUser")
                            , dr.GetInt("published") > 0
                            , dr.GetString("path")
                            , dr.GetString("text")
                            , dr.GetDateTime("createDate")
                            , dr.GetDateTime("updateDate")
                            , dr.GetDateTime("versionDate")
                            , dr.GetString("icon")
                            , dr.GetInt("children") > 0
                            , dr.GetString("alias")
                            , dr.GetString("thumbnail")
                            , dr.GetString("description")
                            , masterContentType
                            , dr.GetInt("contentTypeId")
                            , dr.GetInt("templateId")
                            );

                        // initialize content object
                        InitializeContent(dr.GetInt("ContentType"), dr.GetGuid("versionId"),
                                          dr.GetDateTime("versionDate"), dr.GetString("icon"));

                        // initialize final document properties
                        DateTime tmpReleaseDate = new DateTime();
                        DateTime tmpExpireDate = new DateTime();
                        if (!dr.IsNull("releaseDate"))
                            tmpReleaseDate = dr.GetDateTime("releaseDate");
                        if (!dr.IsNull("expireDate"))
                            tmpExpireDate = dr.GetDateTime("expireDate");

                        InitializeDocument(
                            new User(dr.GetInt("nodeUser"), true),
                            new User(dr.GetInt("documentUser"), true),
                            dr.GetString("documentText"),
                            dr.GetInt("templateId"),
                            tmpReleaseDate,
                            tmpExpireDate,
                            dr.GetDateTime("updateDate"),
                            dr.GetInt("published") > 0
                            );
                    }
                }
            }
        }

        #endregion

        #region Constants and Static members


        // NH: Modified to support SQL CE 4 (doesn't support nested selects)
        private const string SqlOptimizedSingle = @"
Select 
    CASE WHEN (childrenTable.total>0) THEN childrenTable.total ELSE 0 END as Children,
    CASE WHEN (publishedTable.publishedTotal>0) THEN publishedTable.publishedTotal ELSE 0 END as Published,
	cmsContentVersion.VersionId,
    cmsContentVersion.versionDate,	                
	contentTypeNode.uniqueId as ContentTypeGuid, 
	cmsContent.ContentType, cmsContentType.icon, cmsContentType.alias, cmsContentType.thumbnail, cmsContentType.description, cmsContentType.masterContentType, cmsContentType.nodeId as contentTypeId,
	documentUser, coalesce(templateId, cmsDocumentType.templateNodeId) as templateId, cmsDocument.text as DocumentText, releaseDate, expireDate, updateDate, 
	umbracoNode.createDate, umbracoNode.trashed, umbracoNode.parentId, umbracoNode.nodeObjectType, umbracoNode.nodeUser, umbracoNode.level, umbracoNode.path, umbracoNode.sortOrder, umbracoNode.uniqueId, umbracoNode.text 
from 
	umbracoNode 
    inner join cmsContentVersion on cmsContentVersion.contentID = umbracoNode.id
    inner join cmsDocument on cmsDocument.versionId = cmsContentVersion.versionId
    inner join cmsContent on cmsDocument.nodeId = cmsContent.NodeId
    inner join cmsContentType on cmsContentType.nodeId = cmsContent.ContentType
    inner join umbracoNode contentTypeNode on contentTypeNode.id = cmsContentType.nodeId
    left join cmsDocumentType on cmsDocumentType.contentTypeNodeId = cmsContent.contentType and cmsDocumentType.IsDefault = 1 
    /* SQL CE support */
    left outer join (select count(id) as total, parentId from umbracoNode where parentId = @id group by parentId) as childrenTable on childrenTable.parentId = umbracoNode.id
    left outer join (select Count(published) as publishedTotal, nodeId from cmsDocument where published = 1 And nodeId = @id group by nodeId) as publishedTable on publishedTable.nodeId = umbracoNode.id
    /* end SQL CE support */
where umbracoNode.nodeObjectType = @nodeObjectType AND {0}
order by {1}
                ";

        // NH: Had to modify this for SQL CE 4. Only change is that the "coalesce(publishCheck.published,0) as published" didn't work in SQL CE 4
        // because there's already a column called published. I've changed it to isPublished and updated the other places
        //
        // zb-00010 #29443 : removed the following lines + added constraint on cmsDocument.newest in where clause (equivalent + handles duplicate dates)
        //            inner join (select contentId, max(versionDate) as versionDate from cmsContentVersion group by contentId) temp
        //                on cmsContentVersion.contentId = temp.contentId and cmsContentVersion.versionDate = temp.versionDate
        private const string SqlOptimizedMany = @"
                select count(children.id) as children, umbracoNode.id, umbracoNode.uniqueId, umbracoNode.level, umbracoNode.parentId, 
	                cmsDocument.documentUser, coalesce(cmsDocument.templateId, cmsDocumentType.templateNodeId) as templateId, 
	                umbracoNode.path, umbracoNode.sortOrder, coalesce(publishCheck.published,0) as isPublished, umbracoNode.createDate, 
                    cmsDocument.text, cmsDocument.updateDate, cmsContentVersion.versionDate, cmsDocument.releaseDate, cmsDocument.expireDate, cmsContentType.icon, cmsContentType.alias,
	                cmsContentType.thumbnail, cmsContentType.description, cmsContentType.masterContentType, cmsContentType.nodeId as contentTypeId,
                    umbracoNode.nodeUser, umbracoNode.trashed
                from umbracoNode
                    left join umbracoNode children on children.parentId = umbracoNode.id
                    inner join cmsContent on cmsContent.nodeId = umbracoNode.id
                    inner join cmsContentType on cmsContentType.nodeId = cmsContent.contentType
                    inner join cmsContentVersion on cmsContentVersion.contentId = umbracoNode.id
                    inner join cmsDocument on cmsDocument.versionId = cmsContentversion.versionId
                    left join cmsDocument publishCheck on publishCheck.nodeId = cmsContent.nodeID and publishCheck.published = 1
                    left join cmsDocumentType on cmsDocumentType.contentTypeNodeId = cmsContent.contentType and cmsDocumentType.IsDefault = 1
                where umbracoNode.nodeObjectType = @nodeObjectType AND cmsDocument.newest = 1 AND {0}
                group by 
	                umbracoNode.id, umbracoNode.uniqueId, umbracoNode.level, umbracoNode.parentId, cmsDocument.documentUser, 
	                cmsDocument.templateId, cmsDocumentType.templateNodeId, umbracoNode.path, umbracoNode.sortOrder, 
	                coalesce(publishCheck.published,0), umbracoNode.createDate, cmsDocument.text, 
	                cmsContentType.icon, cmsContentType.alias, cmsContentType.thumbnail, cmsContentType.description, 
                    cmsContentType.masterContentType, cmsContentType.nodeId, cmsDocument.updateDate, cmsContentVersion.versionDate, cmsDocument.releaseDate, cmsDocument.expireDate, 
                    umbracoNode.nodeUser, umbracoNode.trashed
                order by {1}
                ";

        private const string SqlOptimizedForPreview = @"
                select umbracoNode.id, umbracoNode.parentId, umbracoNode.level, umbracoNode.sortOrder, cmsDocument.versionId, cmsPreviewXml.xml from cmsDocument
                inner join umbracoNode on umbracoNode.id = cmsDocument.nodeId
                inner join cmsPreviewXml on cmsPreviewXml.nodeId = cmsDocument.nodeId and cmsPreviewXml.versionId = cmsDocument.versionId
                where newest = 1 and trashed = 0 and path like '{0}'
                order by level,sortOrder
 ";

        public static Guid _objectType = new Guid("c66ba18e-eaf3-4cff-8a22-41b16d66a972");

        #endregion

        #region Private properties

        private DateTime _updated;
        private DateTime _release;
        private DateTime _expire;
        private int _template;

        /// <summary>
        /// a backing property for the 'IsDocumentLive()' method
        /// </summary>
        private bool? _isDocumentLive;

        /// <summary>
        /// a backing property for the 'HasPublishedVersion()' method
        /// </summary>
        private bool? _hasPublishedVersion;

        /// <summary>
        /// Used as a value flag to indicate that we've already executed the sql for IsPathPublished()
        /// </summary>
        private bool? _pathPublished;

        private XmlNode _xml;
        private User _creator;
        private User _writer;
        private int? _writerId;
        private bool _optimizedMode;

        /// <summary>
        /// This is used to cache the child documents of Document when the children property
        /// is accessed or enumerated over, this will save alot of database calls.
        /// </summary>
        private IEnumerable<Document> _children = null;

        // special for passing httpcontext object
        //private HttpContext _httpContext;

        // special for tree performance
        private int _userId = -1;

        //private Dictionary<Property, object> _knownProperties = new Dictionary<Property, object>();
        //private Func<KeyValuePair<Property, object>, string, bool> propertyTypeByAlias = (pt, alias) => pt.Key.PropertyType.Alias == alias; 
        #endregion

        #region Static Methods

        /// <summary>
        /// Imports (create) a document from a xmlrepresentation of a document, used by the packager
        /// </summary>
        /// <param name="ParentId">The id to import to</param>
        /// <param name="Creator">Creator of the new document</param>
        /// <param name="Source">Xmlsource</param>
        public static int Import(int ParentId, User Creator, XmlElement Source)
        {
            // check what schema is used for the xml
            bool sourceIsLegacySchema = Source.Name.ToLower() == "node" ? true : false;

            // check whether or not to create a new document
            int id = int.Parse(Source.GetAttribute("id"));
            Document d = null;
            if (Document.IsDocument(id))
            {
                try
                {
                    // if the parent is the same, we'll update the existing document. Else we'll create a new document below
                    d = new Document(id);
                    if (d.ParentId != ParentId)
                        d = null;
                }
                catch { }
            }

            // document either didn't exist or had another parent so we'll create a new one
            if (d == null)
            {
                string nodeTypeAlias = sourceIsLegacySchema ? Source.GetAttribute("nodeTypeAlias") : Source.Name;
                d = MakeNew(
                    Source.GetAttribute("nodeName"),
                    DocumentType.GetByAlias(nodeTypeAlias),
                    Creator,
                    ParentId);
            }
            else
            {
                // update name of the document
                d.Text = Source.GetAttribute("nodeName");
            }

            d.CreateDateTime = DateTime.Parse(Source.GetAttribute("createDate"));

            // Properties
            string propertyXPath = sourceIsLegacySchema ? "data" : "* [not(@isDoc)]";
            foreach (XmlElement n in Source.SelectNodes(propertyXPath))
            {
                string propertyAlias = sourceIsLegacySchema ? n.GetAttribute("alias") : n.Name;
                Property prop = d.getProperty(propertyAlias);
                string propValue = xmlHelper.GetNodeValue(n);

                if (prop != null)
                {
                    // only update real values
                    if (!String.IsNullOrEmpty(propValue))
                    {
                        //test if the property has prevalues, of so, try to convert the imported values so they match the new ones
                        SortedList prevals = cms.businesslogic.datatype.PreValues.GetPreValues(prop.PropertyType.DataTypeDefinition.Id);

                        //Okey we found some prevalue, let's replace the vals with some ids
                        if (prevals.Count > 0)
                        {
                            System.Collections.Generic.List<string> list = new System.Collections.Generic.List<string>(propValue.Split(','));

                            foreach (DictionaryEntry item in prevals)
                            {
                                string pval = ((umbraco.cms.businesslogic.datatype.PreValue)item.Value).Value;
                                string pid = ((umbraco.cms.businesslogic.datatype.PreValue)item.Value).Id.ToString();

                                if (list.Contains(pval))
                                    list[list.IndexOf(pval)] = pid;

                            }

                            //join the list of new values and return it as the new property value
                            System.Text.StringBuilder builder = new System.Text.StringBuilder();
                            bool isFirst = true;

                            foreach (string str in list)
                            {
                                if (!isFirst)
                                    builder.Append(",");

                                builder.Append(str);
                                isFirst = false;
                            }
                            prop.Value = builder.ToString();

                        }
                        else
                            prop.Value = propValue;
                    }
                }
                else
                {
                    Log.Add(LogTypes.Error, d.Id, String.Format("Couldn't import property '{0}' as the property type doesn't exist on this document type", propertyAlias));
                }
            }

            d.Save();

            // Subpages
            string subXPath = sourceIsLegacySchema ? "node" : "* [@isDoc]";
            foreach (XmlElement n in Source.SelectNodes(subXPath))
                Import(d.Id, Creator, n);

            return d.Id;
        }

        /// <summary>
        /// Creates a new document
        /// </summary>
        /// <param name="Name">The name (.Text property) of the document</param>
        /// <param name="dct">The documenttype</param>
        /// <param name="u">The usercontext under which the action are performed</param>
        /// <param name="ParentId">The id of the parent to the document</param>
        /// <returns>The newly created document</returns>
        public static Document MakeNew(string Name, DocumentType dct, User u, int ParentId)
        {
            //allows you to cancel a document before anything goes to the DB
            var newingArgs = new DocumentNewingEventArgs()
            {
                Text = Name,
                DocumentType = dct,
                User = u,
                ParentId = ParentId
            };
            Document.OnNewing(newingArgs);
            if (newingArgs.Cancel)
            {
                return null;
            }


            Guid newId = Guid.NewGuid();

            // Updated to match level from base node
            CMSNode n = new CMSNode(ParentId);
            int newLevel = n.Level;
            newLevel++;

            //create the cms node first
            CMSNode newNode = MakeNew(ParentId, _objectType, u.Id, newLevel, Name, newId);

            //we need to create an empty document and set the underlying text property
            Document tmp = new Document(newId, true);
            tmp.SetText(Name);

            //create the content data for the new document
            tmp.CreateContent(dct);

            //now create the document data
            SqlHelper.ExecuteNonQuery("insert into cmsDocument (newest, nodeId, published, documentUser, versionId, updateDate, Text) "
				+ "values (1, " + tmp.Id + ", 0, " + u.Id + ", @versionId, @updateDate, @text)",
                SqlHelper.CreateParameter("@versionId", tmp.Version),
                SqlHelper.CreateParameter("@updateDate", DateTime.Now),
                SqlHelper.CreateParameter("@text", tmp.Text));

            //read the whole object from the db
            Document d = new Document(newId);

            //event
            NewEventArgs e = new NewEventArgs();
            d.OnNew(e);

            // Log
            Log.Add(LogTypes.New, u, d.Id, "");

            // Run Handler				
            umbraco.BusinessLogic.Actions.Action.RunActionHandlers(d, ActionNew.Instance);

            // Save doc
            d.Save();

            return d;
        }

        /// <summary>
        /// Check if a node is a document
        /// </summary>
        /// <param name="nodeId"></param>
        /// <returns></returns>
        public static bool IsDocument(int nodeId)
        {
            bool isDoc = false;
            using (IRecordsReader dr =
            SqlHelper.ExecuteReader(string.Format("select nodeId from cmsDocument where nodeId = @id"),
                SqlHelper.CreateParameter("@id", nodeId)))
            {

                if (dr.Read())
                {
                    isDoc = true;
                }
            }

            return isDoc;
        }

        
        /// <summary>
        /// Used to get the firstlevel/root documents of the hierachy
        /// </summary>
        /// <returns>Root documents</returns>
        public static Document[] GetRootDocuments()
        {
            Guid[] topNodeIds = TopMostNodeIds(_objectType);

            var docs = new List<Document>();
            for (int i = 0; i < topNodeIds.Length; i++)
            {
                try
                {
                    docs.Add(new Document(topNodeIds[i]));
                }
                catch (Exception ee)
                {
                    Log.Add(LogTypes.Error, new CMSNode(topNodeIds[i]).Id, "GetRootDocuments: " +
                        ee.ToString());
                }
            }
            return docs.ToArray();
        }

        public static int CountSubs(int parentId, bool publishedOnly)
        {
            if (!publishedOnly)
            {
                return CountSubs(parentId);
            }
            else
            {
                return SqlHelper.ExecuteScalar<int>("SELECT COUNT(*) FROM (select distinct umbracoNode.id from umbracoNode INNER JOIN cmsDocument ON cmsDocument.published = 1 and cmsDocument.nodeId = umbracoNode.id WHERE ','+path+',' LIKE '%," + parentId.ToString() + ",%') t");
            }
        }

        /// <summary>
        /// Deletes all documents of a type, will be invoked if a documenttype is deleted.
        /// 
        /// Note: use with care: this method can result in wast amount of data being deleted.
        /// </summary>
        /// <param name="dt">The type of which documents should be deleted</param>
        public static void DeleteFromType(DocumentType dt)
        {
            //get all document for the document type and order by level (top level first)
            var docs = Document.GetDocumentsOfDocumentType(dt.Id)
                .OrderByDescending(x => x.Level);

            foreach (Document doc in docs)
            {
                //before we delete this document, we need to make sure we don't end up deleting other documents that 
                //are not of this document type that are children. So we'll move all of it's children to the trash first.
                foreach (Document c in doc.GetDescendants())
                {
                    if (c.ContentType.Id != dt.Id)
                    {
                        c.MoveToTrash();
                    }
                }

                doc.DeletePermanently();
            }
        }

        public static IEnumerable<Document> GetDocumentsOfDocumentType(int docTypeId)
        {
            var tmp = new List<Document>();
            using (IRecordsReader dr =
                SqlHelper.ExecuteReader(
                                        string.Format(SqlOptimizedMany.Trim(), "cmsContent.contentType = @contentTypeId", "umbracoNode.sortOrder"),
                                        SqlHelper.CreateParameter("@nodeObjectType", Document._objectType),
                                        SqlHelper.CreateParameter("@contentTypeId", docTypeId)))
            {
                while (dr.Read())
                {
                    Document d = new Document(dr.GetInt("id"), true);
                    d.PopulateDocumentFromReader(dr);
                    tmp.Add(d);
                }
            }

            return tmp.ToArray();
        }

        public static void RemoveTemplateFromDocument(int templateId)
        {
            SqlHelper.ExecuteNonQuery("update cmsDocument set templateId = NULL where templateId = @templateId",
                                        SqlHelper.CreateParameter("@templateId", templateId));
        }

        /// <summary>
        /// Performance tuned method for use in the tree
        /// </summary>
        /// <param name="NodeId">The parentdocuments id</param>
        /// <returns></returns>
        public static Document[] GetChildrenForTree(int NodeId)
        {
            var documents = GetChildrenForTreeInternal(NodeId).ToList();
            if (NodeId > 0)
            {
                var parent = new Document(NodeId);
                //update the Published/PathPublished correctly for all documents added to this list
                UpdatePublishedOnDescendants(documents, parent);    
            }
            return documents.ToArray();
        }

        /// <summary>
        /// Performance tuned method for use in the tree
        /// </summary>
        /// <param name="parent">The parent document</param>
        /// <returns></returns>
        public static Document[] GetChildrenForTree(Document parent)
        {
            var documents = GetChildrenForTreeInternal(parent.Id).ToList();
            //update the Published/PathPublished correctly for all documents added to this list
            UpdatePublishedOnDescendants(documents, parent);
            return documents.ToArray();
        }

        public static IEnumerable<Document> GetChildrenForTreeInternal(int nodeId)
        {
            var documents = new List<Document>();
            using (var dr =
                SqlHelper.ExecuteReaderIncreasedTimeout(
                                        string.Format(SqlOptimizedMany.Trim(), "umbracoNode.parentID = @parentId", "umbracoNode.sortOrder"),
                                        SqlHelper.CreateParameter("@nodeObjectType", Document._objectType),
                                        SqlHelper.CreateParameter("@parentId", nodeId)))
            {
                while (dr.Read())
                {
                    var d = new Document(dr.GetInt("id"), true);
                    d.PopulateDocumentFromReader(dr);
                    documents.Add(d);
                }
            }
            return documents;
        }


        public static List<Document> GetChildrenBySearch(int NodeId, string searchString)
        {
            var tmp = new List<Document>();
            using (IRecordsReader dr =
                SqlHelper.ExecuteReader(
                                        string.Format(SqlOptimizedMany.Trim(), "umbracoNode.parentID = @parentId and umbracoNode.text like @search", "umbracoNode.sortOrder"),
                                        SqlHelper.CreateParameter("@nodeObjectType", Document._objectType),
                                        SqlHelper.CreateParameter("@search", searchString),
                                        SqlHelper.CreateParameter("@parentId", NodeId)))
            {
                while (dr.Read())
                {
                    Document d = new Document(dr.GetInt("id"), true);
                    d.PopulateDocumentFromReader(dr);
                    tmp.Add(d);
                }
            }

            return tmp;
        }

        /// <summary>
        /// This will clear out the cmsContentXml table for all Documents (not media or members) and then
        /// rebuild the xml for each Docuemtn item and store it in this table.
        /// </summary>
        /// <remarks>
        /// This method is thread safe
        /// </remarks>
        [MethodImpl(MethodImplOptions.Synchronized)]
        public static void RePublishAll()
        {
            var xd = new XmlDocument();

            //Remove all Documents (not media or members), only Documents are stored in the cmsDocument table
            SqlHelper.ExecuteNonQuery(@"DELETE FROM cmsContentXml WHERE nodeId IN
                                        (SELECT DISTINCT cmsContentXml.nodeId FROM cmsContentXml 
                                            INNER JOIN cmsDocument ON cmsContentXml.nodeId = cmsDocument.nodeId)");
            
            var dr = SqlHelper.ExecuteReader("select nodeId from cmsDocument where published = 1");

            while (dr.Read())
            {
                try
                {
                    //create the document in optimized mode! 
                    // (not sure why we wouldn't always do that ?!)

                    new Document(true, dr.GetInt("nodeId"))
                        .XmlGenerate(xd);

                    //The benchmark results that I found based contructing the Document object with 'true' for optimized
                    //mode, vs using the normal ctor. Clearly optimized mode is better!
                    /*
                     * The average page rendering time (after 10 iterations) for submitting /umbraco/dialogs/republish?xml=true when using 
                     * optimized mode is
                     * 
                     * 0.060400555555556
                     * 
                     * The average page rendering time (after 10 iterations) for submitting /umbraco/dialogs/republish?xml=true when not
                     * using optimized mode is
                     * 
                     * 0.107037777777778
                     *                      
                     * This means that by simply changing this to use optimized mode, it is a 45% improvement!
                     * 
                     */
                }
                catch (Exception ee)
                {
                    LogHelper.Error<Document>("Error generating xml", ee);                    
                }
            }
            dr.Close();
        }

        public static void RegeneratePreviews()
        {
            XmlDocument xd = new XmlDocument();
            IRecordsReader dr = SqlHelper.ExecuteReader("select nodeId from cmsDocument");

            while (dr.Read())
            {
                try
                {
                    new Document(dr.GetInt("nodeId")).SaveXmlPreview(xd);
                }
                catch (Exception ee)
                {
                    Log.Add(LogTypes.Error, User.GetUser(0), dr.GetInt("nodeId"),
                            string.Format("Error generating preview xml: {0}", ee));
                }
            }
            dr.Close();
        }

        /// <summary>
        /// Retrieve a list of documents with an expirationdate greater than today
        /// </summary>
        /// <returns>A list of documents with expirationdates than today</returns>
        public static Document[] GetDocumentsForExpiration()
        {
            ArrayList docs = new ArrayList();
            IRecordsReader dr =
                SqlHelper.ExecuteReader("select distinct nodeId from cmsDocument where published = 1 and not expireDate is null and expireDate <= @today",
                                        SqlHelper.CreateParameter("@today", DateTime.Now));
            while (dr.Read())
                docs.Add(dr.GetInt("nodeId"));
            dr.Close();

            Document[] retval = new Document[docs.Count];
            for (int i = 0; i < docs.Count; i++) retval[i] = new Document((int)docs[i]);
            return retval;
        }

        /// <summary>
        /// Retrieve a list of documents with with releasedate greater than today
        /// </summary>
        /// <returns>Retrieve a list of documents with with releasedate greater than today</returns>
        public static Document[] GetDocumentsForRelease()
        {
            ArrayList docs = new ArrayList();
            IRecordsReader dr = SqlHelper.ExecuteReader("select distinct nodeId, level, sortOrder from cmsDocument inner join umbracoNode on umbracoNode.id = cmsDocument.nodeId where newest = 1 and not releaseDate is null and releaseDate <= @today order by [level], sortOrder",
                                        SqlHelper.CreateParameter("@today", DateTime.Now));
            while (dr.Read())
                docs.Add(dr.GetInt("nodeId"));
            dr.Close();


            Document[] retval = new Document[docs.Count];
            for (int i = 0; i < docs.Count; i++) retval[i] = new Document((int)docs[i]);

            return retval;
        }

        #endregion

        #region Public Properties

        /// <summary>
        /// Gets a value indicating whether the document was constructed for the optimized mode
        /// </summary>
        /// <value><c>true</c> if the document is working in the optimized mode; otherwise, <c>false</c>.</value>
        public bool OptimizedMode
        {
            get
            {
                return this._optimizedMode;
            }
        }

        /// <summary>
        /// The id of the user whom created the document
        /// </summary>
        public int UserId
        {
            get
            {
                if (_userId == -1)
                    _userId = User.Id;

                return _userId;
            }
        }

        /// <summary>
        /// Gets the user who created the document.
        /// </summary>
        /// <value>The creator.</value>
        public User Creator
        {
            get
            {
                if (_creator == null)
                {
                    _creator = User;
                }
                return _creator;
            }
        }

        /// <summary>
        /// Gets the writer.
        /// </summary>
        /// <value>The writer.</value>
        public User Writer
        {
            get
            {
                if (_writer == null)
                {
                    if (!_writerId.HasValue)
                    {
                        throw new NullReferenceException("Writer ID has not been specified for this document");
                    }
                    _writer = User.GetUser(_writerId.Value);
                }
                return _writer;
            }
        }

        /// <summary>
        /// The current HTTPContext
        /// </summary>
        [Obsolete("DO NOT USE THIS! Get the HttpContext via regular ASP.Net methods instead")]
        public HttpContext HttpContext
        {
            set { /*THERE IS NO REASON TO DO THIS. _httpContext = value; */}
            get
            {
                //if (_httpContext == null)
                //    _httpContext = HttpContext.Current;
                //return _httpContext;
                return System.Web.HttpContext.Current;
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether the document is published.
        /// </summary>
		/// <remarks>A document can be published yet not visible, because of one or more of its
		/// parents being unpublished. Use <c>PathPublished</c> to get a value indicating whether
		/// the node and all its parents are published, and therefore whether the node is visible.</remarks>
        public bool Published
        {
            get
            {
                //this is always the same as HasPublishedVersion in 4.x
                return HasPublishedVersion();
            }
            set
            {
                _hasPublishedVersion = value;
                SqlHelper.ExecuteNonQuery(
                    string.Format("update cmsDocument set published = {0} where nodeId = {1}", Id, value ? 1 : 0));
            }
        }

        /// <summary>
        /// Will return true if the document is published and live on the front-end.
        /// </summary>
        /// <returns></returns>
        internal bool IsDocumentLive()
        {
            if (!_isDocumentLive.HasValue)
            {
                // get all nodes in the path to the document, and get all matching published documents
                // the difference should be zero if everything is published
                // test nodeObjectType to make sure we only count _content_ nodes
                var sql = @"select count(node.id) - count(doc.nodeid)
from umbracoNode as node 
left join cmsDocument as doc on (node.id=doc.nodeId and doc.published=1)
where (('" + Path + ",' like " + SqlHelper.Concat("node.path", "',%'") + @")
 or ('" + Path + @"' = node.path)) and node.id <> -1
and node.nodeObjectType=@nodeObjectType";

                var count = SqlHelper.ExecuteScalar<int>(sql, SqlHelper.CreateParameter("@nodeObjectType", Document._objectType));
                _isDocumentLive = (count == 0);
            }
            return _isDocumentLive.Value;
        }

        /// <summary>
        /// Returns true if the document's ancestors are all published
        /// </summary>
        /// <returns></returns>
        /// <remarks>
        /// </remarks>
		public bool PathPublished
		{
			get
			{
                //check our cached value for this object
                if (!_pathPublished.HasValue)
                {
                    // get all nodes in the path to the document, and get all matching published documents
                    // the difference should be zero if everything is published
                    // test nodeObjectType to make sure we only count _content_ nodes
                    var sql = @"select count(node.id) - count(doc.nodeid)
from umbracoNode as node 
left join cmsDocument as doc on (node.id=doc.nodeId and doc.published=1)
where '" + Path + ",' like " + SqlHelper.Concat("node.path", "',%'") + @"
and node.nodeObjectType=@nodeObjectType";

                    var count = SqlHelper.ExecuteScalar<int>(sql, SqlHelper.CreateParameter("@nodeObjectType", Document._objectType));
                    _pathPublished = (count == 0);
                }

                return _pathPublished.Value;
			}
            internal set { _pathPublished = value; }
		}

        public override string Text
        {
            get
            {
                return base.Text;
            }
            set
            {
                value = value.Trim();
                base.Text = value;
                SqlHelper.ExecuteNonQuery("update cmsDocument set text = @text where versionId = @versionId",
                                          SqlHelper.CreateParameter("@text", value),
                                          SqlHelper.CreateParameter("@versionId", Version));
                //CMSNode c = new CMSNode(Id);
                //c.Text = value;
            }
        }

        /// <summary>
        /// The date of the last update of the document
        /// </summary>
        public DateTime UpdateDate
        {
            get { return _updated; }
            set
            {
                _updated = value;
                SqlHelper.ExecuteNonQuery("update cmsDocument set updateDate = @value where versionId = @versionId",
                                          SqlHelper.CreateParameter("@value", value),
                                          SqlHelper.CreateParameter("@versionId", Version));
            }
        }

        /// <summary>
        /// A datestamp which indicates when a document should be published, used in automated publish/unpublish scenarios
        /// </summary>
        public DateTime ReleaseDate
        {
            get { return _release; }
            set
            {
                _release = value;

                if (_release.Year != 1 || _release.Month != 1 || _release.Day != 1)
                    SqlHelper.ExecuteNonQuery("update cmsDocument set releaseDate = @value where versionId = @versionId",
                                              SqlHelper.CreateParameter("@value", value),
                                              SqlHelper.CreateParameter("@versionId", Version));
                else
                    SqlHelper.ExecuteNonQuery("update cmsDocument set releaseDate = NULL where versionId = @versionId",
                                              SqlHelper.CreateParameter("@versionId", Version));
            }
        }

        /// <summary>
        /// A datestamp which indicates when a document should be unpublished, used in automated publish/unpublish scenarios
        /// </summary>
        public DateTime ExpireDate
        {
            get { return _expire; }
            set
            {
                _expire = value;

                if (_expire.Year != 1 || _expire.Month != 1 || _expire.Day != 1)
                    SqlHelper.ExecuteNonQuery("update cmsDocument set expireDate = @value where versionId=@versionId",
                                              SqlHelper.CreateParameter("@value", value),
                                              SqlHelper.CreateParameter("@versionId", Version));
                else
                    SqlHelper.ExecuteNonQuery("update cmsDocument set expireDate = NULL where versionId=@versionId",
                                              SqlHelper.CreateParameter("@versionId", Version));
            }
        }

        /// <summary>
        /// The id of the template associated to the document
        /// 
        /// When a document is created, it will get have default template given by it's documenttype,
        /// an editor is able to assign alternative templates (allowed by it's the documenttype)
        /// 
        /// You are always able to override the template in the runtime by appending the following to the querystring to the Url:
        /// 
        /// ?altTemplate=[templatealias]
        /// </summary>
        public int Template
        {
            get { return _template; }
            set
            {
                _template = value;
                if (value == 0)
                {
                    SqlHelper.ExecuteNonQuery("update cmsDocument set templateId = @value where versionId = @versionId",
                                              SqlHelper.CreateParameter("@value", DBNull.Value),
                                              SqlHelper.CreateParameter("@versionId", Version));
                }
                else
                {
                    SqlHelper.ExecuteNonQuery("update cmsDocument set templateId = @value where versionId = @versionId",
                                              SqlHelper.CreateParameter("@value", _template),
                                              SqlHelper.CreateParameter("@versionId", Version));
                }
            }
        }

        /// <summary>
        /// A collection of documents imidiately underneath this document ie. the childdocuments
        /// </summary>
        public new Document[] Children
        {
            get
            {
                //cache the documents children so that this db call doesn't have to occur again
                if (this._children == null)
                    this._children = GetChildrenForTree(this);

                return this._children.ToArray();
            }
        }


        /// <summary>
        /// Indexed property to return the property value by name
        /// </summary>
        /// <param name="alias"></param>
        /// <returns></returns>
        //public object this[string alias]
        //{
        //    get
        //    {
        //        if (this._optimizedMode)
        //        {
        //            return this._knownProperties.Single(p => propertyTypeByAlias(p, alias)).Value;
        //        }
        //        else
        //        {
        //            return this.getProperty(alias).Value;
        //        }
        //    }
        //    set
        //    {
        //        if (this._optimizedMode)
        //        {
        //            if (this._knownProperties.SingleOrDefault(p => propertyTypeByAlias(p, alias)).Key == null)
        //            {
        //                var pt = this.getProperty(alias);

        //                this._knownProperties.Add(pt, pt.Value);
        //            }
        //            else
        //            {
        //                var pt = this._knownProperties.Single(p => propertyTypeByAlias(p, alias)).Key;
        //                this._knownProperties[pt] = value;
        //            }
        //        }
        //        else
        //        {
        //            this.getProperty(alias).Value = value;
        //        }
        //    }
        //}

        #endregion

        #region Public Methods

        /// <summary>
        /// Executes handlers and events for the Send To Publication action.
        /// </summary>
        /// <param name="u">The User</param>
        public bool SendToPublication(User u)
        {
            SendToPublishEventArgs e = new SendToPublishEventArgs();
            FireBeforeSendToPublish(e);
            if (!e.Cancel)
            {
                BusinessLogic.Log.Add(BusinessLogic.LogTypes.SendToPublish, u, this.Id, "");

                BusinessLogic.Actions.Action.RunActionHandlers(this, ActionToPublish.Instance);

                FireAfterSendToPublish(e);
                return true;
            }

            return false;

        }

        /// <summary>
        /// Publishing a document
        /// A xmlrepresentation of the document and its data are exposed to the runtime data
        /// (an xmlrepresentation is added -or updated if the document previously are published) ,
        /// this will lead to a new version of the document being created, for continuing editing of
        /// the data.
        /// </summary>
        /// <param name="u">The usercontext under which the action are performed</param>
        public void Publish(User u)
        {
            PublishWithResult(u);
        }

        /// <summary>
        /// Publishing a document
        /// A xmlrepresentation of the document and its data are exposed to the runtime data
        /// (an xmlrepresentation is added -or updated if the document previously are published) ,
        /// this will lead to a new version of the document being created, for continuing editing of
        /// the data.
        /// </summary>
        /// <param name="u">The usercontext under which the action are performed</param>
        /// <returns>True if the publishing succeed. Possible causes for not publishing is if an event aborts the publishing</returns>
        /// <remarks>
        /// This method needs to be marked with [MethodImpl(MethodImplOptions.Synchronized)]
        /// because we execute multiple queries affecting the same data, if two thread are to do this at the same time for the same node we may have problems
        /// </remarks>
        [MethodImpl(MethodImplOptions.Synchronized)]
        public bool PublishWithResult(User u)
        {
            PublishEventArgs e = new PublishEventArgs();
            FireBeforePublish(e);

            if (!e.Cancel)
            {

                // make a lookup to see if template is 0 as the template is not initialized in the optimized
                // Document.Children method which is used in PublishWithChildrenWithResult methhod
                if (_template == 0)
                {
                    _template = new DocumentType(this.ContentType.Id).DefaultTemplate;
                }

                _hasPublishedVersion = true;
                string tempVersion = Version.ToString();
                DateTime versionDate = DateTime.Now;
                Guid newVersion = createNewVersion(versionDate);
                
                Log.Add(LogTypes.Publish, u, Id, "");

                //PPH make sure that there is only 1 newest node, this is important in regard to schedueled publishing...
                SqlHelper.ExecuteNonQuery("update cmsDocument set newest = 0 where nodeId = " + Id);

                SqlHelper.ExecuteNonQuery("insert into cmsDocument (newest, nodeId, published, documentUser, versionId, updateDate, Text, TemplateId) "
					+ "values (1, @id, 0, @userId, @versionId, @updateDate, @text, @template)",
                    SqlHelper.CreateParameter("@id", Id),
                    SqlHelper.CreateParameter("@userId", u.Id),
                    SqlHelper.CreateParameter("@versionId", newVersion),
                    SqlHelper.CreateParameter("@updateDate", versionDate),
                    SqlHelper.CreateParameter("@text", Text),
                    SqlHelper.CreateParameter("@template", _template > 0 ? (object)_template : (object)DBNull.Value) //pass null in if the template doesn't have a valid id
					);

                SqlHelper.ExecuteNonQuery("update cmsDocument set published = 0 where nodeId = " + Id);
                SqlHelper.ExecuteNonQuery("update cmsDocument set published = 1, newest = 0 where versionId = @versionId",
                                            SqlHelper.CreateParameter("@versionId", tempVersion));

                // update release and expire dates
                Document newDoc = new Document(Id, newVersion);
                if (ReleaseDate != new DateTime())
                    newDoc.ReleaseDate = ReleaseDate;
                if (ExpireDate != new DateTime())
                    newDoc.ExpireDate = ExpireDate;

                // Update xml in db using the new document (has correct version date)
                newDoc.XmlGenerate(new XmlDocument());

                FireAfterPublish(e);

                return true;
            }
            else
            {
                return false;
            }
        }

        public bool PublishWithChildrenWithResult(User u)
        {
            if (PublishWithResult(u))
            {
                foreach (cms.businesslogic.web.Document dc in Children.ToList())
                {
                    dc.PublishWithChildrenWithResult(u);
                }
            }
            else
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Rollbacks a document to a previous version, this will create a new version of the document and copy
        /// all of the old documents data.
        /// </summary>
        /// <param name="u">The usercontext under which the action are performed</param>
        /// <param name="VersionId">The unique Id of the version to roll back to</param>
        public void RollBack(Guid VersionId, User u)
        {
            RollBackEventArgs e = new RollBackEventArgs();
            FireBeforeRollBack(e);

            if (!e.Cancel)
            {
                DateTime versionDate = DateTime.Now;
                Guid newVersion = createNewVersion(versionDate);
 
                if (_template != 0)
                {
                    SqlHelper.ExecuteNonQuery("insert into cmsDocument (nodeId, published, documentUser, versionId, updateDate, Text, TemplateId) "
						+ "values (@nodeId, 0, @userId, @versionId, @updateDate, @text, @templateId)",
						SqlHelper.CreateParameter("@nodeId", Id),
						SqlHelper.CreateParameter("@userId", u.Id),
                        SqlHelper.CreateParameter("@versionId", newVersion),
                        SqlHelper.CreateParameter("@updateDate", versionDate),
                        SqlHelper.CreateParameter("@text", Text),
						SqlHelper.CreateParameter("@templateId", _template));
                }
                else
                {
                    SqlHelper.ExecuteNonQuery("insert into cmsDocument (nodeId, published, documentUser, versionId, updateDate, Text) "
						+ "values (@nodeId, 0, @userId, @versionId, @updateDate, @text)",
						SqlHelper.CreateParameter("@nodeId", Id),
						SqlHelper.CreateParameter("@userId", u.Id),
                        SqlHelper.CreateParameter("@versionId", newVersion),
                        SqlHelper.CreateParameter("@updateDate", versionDate),
                        SqlHelper.CreateParameter("@text", Text));
                }

                // Get new version
                Document dNew = new Document(Id, newVersion);

                // Old version
                Document dOld = new Document(Id, VersionId);

                // Revert title
                dNew.Text = dOld.Text;

                // Revert all properties
                var props = dOld.getProperties;
                foreach (Property p in props)
                    try
                    {
                        dNew.getProperty(p.PropertyType).Value = p.Value;
                    }
                    catch
                    {
                        // property doesn't exists
                    }

                FireAfterRollBack(e);
            }
        }

        /// <summary>
        /// Recursive publishing.
        /// 
        /// Envoking this method will publish the documents and all children recursive.
        /// </summary>
        /// <param name="u">The usercontext under which the action are performed</param>
        public void PublishWithSubs(User u)
        {

            PublishEventArgs e = new PublishEventArgs();
            FireBeforePublish(e);

            if (!e.Cancel)
            {
                _hasPublishedVersion = true;
                string tempVersion = Version.ToString();
                DateTime versionDate = DateTime.Now;
                Guid newVersion = createNewVersion(versionDate);

                SqlHelper.ExecuteNonQuery("insert into cmsDocument (nodeId, published, documentUser, versionId, updateDate, Text) "
					+ "values (" + Id + ", 0, " + u.Id + ", @versionId, @updateDate, @text)",
                    SqlHelper.CreateParameter("@versionId", newVersion),
                    SqlHelper.CreateParameter("@updateDate", versionDate),
                    SqlHelper.CreateParameter("@text", Text));

                SqlHelper.ExecuteNonQuery("update cmsDocument set published = 0 where nodeId = " + Id);
                SqlHelper.ExecuteNonQuery("update cmsDocument set published = 1 where versionId = @versionId", SqlHelper.CreateParameter("@versionId", tempVersion));

                BusinessLogic.Log.Add(LogTypes.Debug, -1, newVersion.ToString() + " - " + Id.ToString());

                // Update xml in db
                XmlGenerate(new XmlDocument());

                foreach (Document dc in Children.ToList())
                    dc.PublishWithSubs(u);

                FireAfterPublish(e);
            }
        }

        public void UnPublish()
        {
            var e = new UnPublishEventArgs();

            FireBeforeUnPublish(e);

            if (!e.Cancel)
            {
                SqlHelper.ExecuteNonQuery(string.Format("update cmsDocument set published = 0 where nodeId = {0}", Id));

                _hasPublishedVersion = false;

                FireAfterUnPublish(e);
            }
        }

        /// <summary>
        /// Used to persist object changes to the database. 
        /// </summary>
        public override void Save()
        {
            var e = new SaveEventArgs();
            FireBeforeSave(e);

            if (!e.Cancel)
            {

                //if (this._optimizedMode)
                //{
                //    foreach (var property in this._knownProperties)
                //    {
                //        var pt = property.Key;
                //        pt.Value = property.Value;
                //    }
                //}

                UpdateDate = DateTime.Now; //set the updated date to now

                base.Save();
                // update preview xml
                SaveXmlPreview(new XmlDocument());

                FireAfterSave(e);
            }
        }

        /// <summary>
        /// Returns true if the document has a published item in the database but is not in the recycle bin
        /// </summary>
        /// <returns></returns>
        /// <remarks>
        /// This will still return true if this document is not published on the front-end in some cases if one of it's ancestors are 
        /// not-published. If you have a published document and unpublish one of it's ancestors, it will retain it's published flag in the
        /// database.
        /// </remarks>
        public bool HasPublishedVersion()
        {
            //lazy load the value if it is not set.
            if (!_hasPublishedVersion.HasValue)
            {
                var count = SqlHelper.ExecuteScalar<int>(@"
select Count(published) as CountOfPublished 
from cmsDocument 
inner join umbracoNode on cmsDocument.nodeId = umbracoNode.id
where published = 1 And nodeId = @nodeId And trashed = 0", SqlHelper.CreateParameter("@nodeId", Id));

                _hasPublishedVersion = count > 0;
            }
            return _hasPublishedVersion.Value;
        }

        /// <summary>
        /// Pending changes means that there have been property/data changes since the last published version.
        /// This is determined by the comparing the version date to the updated date. if they are different by more than 2 seconds, 
        /// then this is considered a change.
        /// </summary>
        /// <returns></returns>
        public bool HasPendingChanges()
        {
            double timeDiff = new TimeSpan(UpdateDate.Ticks - VersionDate.Ticks).TotalMilliseconds;
            return timeDiff > 2000;
        }

        /// <summary>
        /// Used for rolling back documents to a previous version
        /// </summary>
        /// <returns> Previous published versions of the document</returns>
        public DocumentVersionList[] GetVersions()
        {
            ArrayList versions = new ArrayList();
            using (IRecordsReader dr =
                SqlHelper.ExecuteReader("select documentUser, versionId, updateDate, text from cmsDocument where nodeId = @nodeId order by updateDate",
                                        SqlHelper.CreateParameter("@nodeId", Id)))
            {
                while (dr.Read())
                {
                    DocumentVersionList dv =
                        new DocumentVersionList(dr.GetGuid("versionId"),
                                                dr.GetDateTime("updateDate"),
                                                dr.GetString("text"),
                                                User.GetUser(dr.GetInt("documentUser")));
                    versions.Add(dv);
                }
            }

            DocumentVersionList[] retVal = new DocumentVersionList[versions.Count];
            int i = 0;
            foreach (DocumentVersionList dv in versions)
            {
                retVal[i] = dv;
                i++;
            }
            return retVal;
        }

        /// <summary>
        /// Returns the published version of this document
        /// </summary>
        /// <returns>The published version of this document</returns>
        public DocumentVersionList GetPublishedVersion()
        {
            using (IRecordsReader dr =
                SqlHelper.ExecuteReader("select top 1 documentUser, versionId, updateDate, text from cmsDocument where nodeId = @nodeId and published = 1 order by updateDate desc",
                                        SqlHelper.CreateParameter("@nodeId", Id)))
            {
                if (dr.Read())
                {
                    return new DocumentVersionList(dr.GetGuid("versionId"),
                                                dr.GetDateTime("updateDate"),
                                                dr.GetString("text"),
                                                User.GetUser(dr.GetInt("documentUser")));
                }
            }

            return null;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns>Returns a breadcrumlike path for the document like: /ancestorname/ancestorname</returns>
        public string GetTextPath()
        {
            string tempPath = "";
            string[] splitPath = Path.Split(".".ToCharArray());
            for (int i = 1; i < Level; i++)
            {
                tempPath += new Document(int.Parse(splitPath[i])).Text + "/";
            }
            if (tempPath.Length > 0)
                tempPath = tempPath.Substring(0, tempPath.Length - 1);
            return tempPath;
        }

        /// <summary>
        /// Creates a new document of the same type and copies all data from the current onto it. Due to backwards compatibility we can't return
        /// the new Document, but it's included in the CopyEventArgs.Document if you subscribe to the AfterCopy event
        /// </summary>
        /// <param name="CopyTo">The parentid where the document should be copied to</param>
        /// <param name="u">The usercontext under which the action are performed</param>
        public Document Copy(int CopyTo, User u)
        {
            return Copy(CopyTo, u, false);
        }

        /// <summary>
        /// Creates a new document of the same type and copies all data from the current onto it. Due to backwards compatibility we can't return
        /// the new Document, but it's included in the CopyEventArgs.Document if you subscribe to the AfterCopy event
        /// </summary>
        /// <param name="CopyTo"></param>
        /// <param name="u"></param>
        /// <param name="RelateToOrignal"></param>
        public Document Copy(int CopyTo, User u, bool RelateToOrignal)
        {
            var fs = FileSystemProviderManager.Current.GetFileSystemProvider<MediaFileSystem>();

            CopyEventArgs e = new CopyEventArgs();
            e.CopyTo = CopyTo;
            FireBeforeCopy(e);
            Document newDoc = null;

            if (!e.Cancel)
            {
                // Make the new document
                newDoc = MakeNew(Text, new DocumentType(ContentType.Id), u, CopyTo);

                if (newDoc != null)
                {
                    // update template if a template is set
                    if (this.Template > 0)
                        newDoc.Template = Template;

                    //update the trashed property as it could be copied inside the recycle bin
                    newDoc.IsTrashed = this.IsTrashed;

                    // Copy the properties of the current document
                    var props = GenericProperties;
                    foreach (Property p in props)
                    {
                        //copy file if it's an upload property (so it doesn't get removed when original doc get's deleted)

                        IDataType tagsField = new Factory().GetNewObject(new Guid("4023e540-92f5-11dd-ad8b-0800200c9a66"));
                        IDataType uploadField = new Factory().GetNewObject(new Guid("5032a6e6-69e3-491d-bb28-cd31cd11086c"));

                        if (p.PropertyType.DataTypeDefinition.DataType.Id == uploadField.Id
                        && p.Value.ToString() != ""
                        && fs.FileExists(fs.GetRelativePath(p.Value.ToString())))
                        {
                            var currentPath = fs.GetRelativePath(p.Value.ToString());

                            var propId = newDoc.getProperty(p.PropertyType.Alias).Id;
                            var newPath = fs.GetRelativePath(propId, System.IO.Path.GetFileName(currentPath));

                            fs.CopyFile(currentPath, newPath);

                            newDoc.getProperty(p.PropertyType.Alias).Value = fs.GetUrl(newPath);

                            //copy thumbs
                            foreach (var thumbPath in fs.GetThumbnails(currentPath))
                            {
                                var newThumbPath = fs.GetRelativePath(propId, System.IO.Path.GetFileName(thumbPath));
                                fs.CopyFile(thumbPath, newThumbPath);
                            }

                        }
                        else if (p.PropertyType.DataTypeDefinition.DataType.Id == tagsField.Id &&
                                 p.Value.ToString() != "")
                        {
                            //Find tags from the original and add them to the new document
                            var tags = Tags.Tag.GetTags(this.Id);
                            foreach (var tag in tags)
                            {
                                Tags.Tag.AddTagsToNode(newDoc.Id, tag.TagCaption, tag.Group);
                            }
                        }
                        else
                        {
                            newDoc.getProperty(p.PropertyType.Alias).Value = p.Value;
                        }

                    }

                    // Relate?
                    if (RelateToOrignal)
                    {
                        Relation.MakeNew(Id, newDoc.Id, RelationType.GetByAlias("relateDocumentOnCopy"), "");

                        // Add to audit trail
                        Log.Add(LogTypes.Copy, u, newDoc.Id, "Copied and related from " + Text + " (id: " + Id.ToString() + ")");
                    }


                    // Copy the children
                    //store children array here because iterating over an Array object is very inneficient.
                    var c = Children;
                    foreach (Document d in c)
                        d.Copy(newDoc.Id, u, RelateToOrignal);

                    e.NewDocument = newDoc;
                }

                FireAfterCopy(e);

            }

            return newDoc;
        }

        /// <summary>
        /// Puts the current document in the trash
        /// </summary>
        public override void delete()
        {
            MoveToTrash();
        }

        /// <summary>
        /// With either move the document to the trash or permanently remove it from the database.
        /// </summary>
        /// <param name="deletePermanently">flag to set whether or not to completely remove it from the database or just send to trash</param>
        public void delete(bool deletePermanently)
        {
            if (!deletePermanently)
            {
                MoveToTrash();
            }
            else
            {
                DeletePermanently();
            }
        }

        /// <summary>
        /// Returns all descendants that are published on the front-end (hava a full published path)
        /// </summary>
        /// <returns></returns>
        internal IEnumerable<Document> GetPublishedDescendants()
        {            
            var documents = new List<Document>();
            using (var dr = SqlHelper.ExecuteReader(
                                        string.Format(SqlOptimizedMany.Trim(), "umbracoNode.path LIKE '%," + this.Id + ",%'", "umbracoNode.level"),
                                        SqlHelper.CreateParameter("@nodeObjectType", Document._objectType)))
            {
                while (dr.Read())
                {
                    var d = new Document(dr.GetInt("id"), true);
                    d.PopulateDocumentFromReader(dr);
                    documents.Add(d);  
                }
            }

            //update the Published/PathPublished correctly for all documents added to this list
            UpdatePublishedOnDescendants(documents, this);

            //now, we only want to return any descendants that have a Published = true (full published path)
            return documents.Where(x => x.Published);
        } 

        /// <summary>
        /// Returns all decendants of the current document
        /// </summary>
        /// <returns></returns>
        public override IEnumerable GetDescendants()
        {
            var documents = new List<Document>();
            using (IRecordsReader dr = SqlHelper.ExecuteReader(
                                        string.Format(SqlOptimizedMany.Trim(), "umbracoNode.path LIKE '%," + this.Id + ",%'", "umbracoNode.level"),
                                        SqlHelper.CreateParameter("@nodeObjectType", Document._objectType)))
            {
                while (dr.Read())
                {
                    var d = new Document(dr.GetInt("id"), true);
                    d.PopulateDocumentFromReader(dr);
                    documents.Add(d);
                }
            }

            //update the Published/PathPublished correctly for all documents added to this list
            UpdatePublishedOnDescendants(documents, this);

            return documents.ToArray();
        }

        /// <summary>
        /// Refreshes the xml, used when publishing data on a document which already is published
        /// </summary>
        /// <param name="xd">The source xmldocument</param>
        /// <param name="x">The previous xmlrepresentation of the document</param>
        public void XmlNodeRefresh(XmlDocument xd, ref XmlNode x)
        {
            x.Attributes.RemoveAll();
            foreach (XmlNode xDel in x.SelectNodes("./data"))
                x.RemoveChild(xDel);

            XmlPopulate(xd, ref x, false);
        }

        /// <summary>
        /// Creates an xmlrepresentation of the document and saves it to the database
        /// </summary>
        /// <param name="xd"></param>
        public override void XmlGenerate(XmlDocument xd)
        {
            XmlNode x = generateXmlWithoutSaving(xd);
            /*
                        if (!UmbracoSettings.UseFriendlyXmlSchema)
                        {
                        } else
                        {
                            XmlNode childNodes = xmlHelper.addTextNode(xd, "data", "");
                            x.AppendChild(childNodes);
                            XmlPopulate(xd, ref childNodes, false);
                        }
            */


            // Save to db
            saveXml(x);
        }

        /// <summary>
        /// A xmlrepresentaion of the document, used when publishing/exporting the document, 
        /// 
        /// Optional: Recursive get childdocuments xmlrepresentation
        /// </summary>
        /// <param name="xd">The xmldocument</param>
        /// <param name="Deep">Recursive add of childdocuments</param>
        /// <returns></returns>
        public override XmlNode ToXml(XmlDocument xd, bool Deep)
        {
            if (Published)
            {
                if (_xml == null)
                {
                    // Load xml from db if _xml hasn't been loaded yet
                    _xml = importXml();

                    // Generate xml if xml still null (then it hasn't been initialized before)
                    if (_xml == null)
                    {
                        XmlGenerate(new XmlDocument());
                        _xml = importXml();
                    }
                }

                XmlNode x = xd.ImportNode(_xml, true);

                if (Deep)
                {
                    var c = Children;
                    foreach (Document d in c)
                    {
                        if (d.Published)
                            x.AppendChild(d.ToXml(xd, true));
                    }
                }

                return x;
            }
            else
                return null;
        }

        /// <summary>
        /// Populate a documents xmlnode
        /// </summary>
        /// <param name="xd">Xmldocument context</param>
        /// <param name="x">The node to fill with data</param>
        /// <param name="Deep">If true the documents childrens xmlrepresentation will be appended to the Xmlnode recursive</param>
        public override void XmlPopulate(XmlDocument xd, ref XmlNode x, bool Deep)
        {
            string urlName = this.Text;
            foreach (Property p in GenericProperties)
                if (p != null)
                {
                    x.AppendChild(p.ToXml(xd));
                    if (p.PropertyType.Alias == "umbracoUrlName" && p.Value.ToString().Trim() != string.Empty)
                        urlName = p.Value.ToString();
                }

            // attributes
            x.Attributes.Append(addAttribute(xd, "id", Id.ToString()));
            //            x.Attributes.Append(addAttribute(xd, "version", Version.ToString()));
            if (Level > 1)
                x.Attributes.Append(addAttribute(xd, "parentID", Parent.Id.ToString()));
            else
                x.Attributes.Append(addAttribute(xd, "parentID", "-1"));
            x.Attributes.Append(addAttribute(xd, "level", Level.ToString()));
            x.Attributes.Append(addAttribute(xd, "writerID", Writer.Id.ToString()));
            x.Attributes.Append(addAttribute(xd, "creatorID", Creator.Id.ToString()));
            if (ContentType != null)
                x.Attributes.Append(addAttribute(xd, "nodeType", ContentType.Id.ToString()));
            x.Attributes.Append(addAttribute(xd, "template", _template.ToString()));
            x.Attributes.Append(addAttribute(xd, "sortOrder", sortOrder.ToString()));
            x.Attributes.Append(addAttribute(xd, "createDate", CreateDateTime.ToString("s")));
            x.Attributes.Append(addAttribute(xd, "updateDate", VersionDate.ToString("s")));
            x.Attributes.Append(addAttribute(xd, "nodeName", Text));
            x.Attributes.Append(addAttribute(xd, "urlName", url.FormatUrl(urlName.ToLower())));
            x.Attributes.Append(addAttribute(xd, "writerName", Writer.Name));
            x.Attributes.Append(addAttribute(xd, "creatorName", Creator.Name.ToString()));
            if (ContentType != null && UmbracoSettings.UseLegacyXmlSchema)
                x.Attributes.Append(addAttribute(xd, "nodeTypeAlias", ContentType.Alias));
            x.Attributes.Append(addAttribute(xd, "path", Path));

            if (!UmbracoSettings.UseLegacyXmlSchema)
            {
                x.Attributes.Append(addAttribute(xd, "isDoc", ""));
            }

            if (Deep)
            {
                //store children array here because iterating over an Array object is very inneficient.
                var c = Children;
                foreach (Document d in c)
                {
                    XmlNode xml = d.ToXml(xd, true);
                    if (xml != null)
                    {
                        x.AppendChild(xml);
                    }
                    else
                    {
                        Log.Add(LogTypes.System, d.Id, "Document not published so XML cannot be generated");
                    }
                }

            }
        }

        /// <summary>
        /// This is a specialized method which literally just makes sure that the sortOrder attribute of the xml
        /// that is stored in the database is up to date.
        /// </summary>
        public void refreshXmlSortOrder()
        {
            if (Published)
            {
                if (_xml == null)
                    // Load xml from db if _xml hasn't been loaded yet
                    _xml = importXml();

                // Generate xml if xml still null (then it hasn't been initialized before)
                if (_xml == null)
                {
                    XmlGenerate(new XmlDocument());
                    _xml = importXml();
                }
                else
                {
                    // Update the sort order attr
                    _xml.Attributes.GetNamedItem("sortOrder").Value = sortOrder.ToString();
                    saveXml(_xml);
                }

            }

        }

        public override List<CMSPreviewNode> GetNodesForPreview(bool childrenOnly)
        {
            List<CMSPreviewNode> nodes = new List<CMSPreviewNode>();

            string pathExp = childrenOnly ? Path + ",%" : Path;

            IRecordsReader dr = SqlHelper.ExecuteReader(String.Format(SqlOptimizedForPreview, pathExp));
            while (dr.Read())
                nodes.Add(new CMSPreviewNode(dr.GetInt("id"), dr.GetGuid("versionId"), dr.GetInt("parentId"), dr.GetShort("level"), dr.GetInt("sortOrder"), dr.GetString("xml")));
            dr.Close();

            return nodes;
        }

        public override XmlNode ToPreviewXml(XmlDocument xd)
        {
            if (!PreviewExists(Version))
            {
                SaveXmlPreview(xd);
            }
            return GetPreviewXml(xd, Version);
        }

        /// <summary>
        /// Method to remove an assigned template from a document
        /// </summary>
        public void RemoveTemplate()
        {
            Template = 0;
        }

        #endregion

        #region Protected Methods
        protected override void setupNode()
        {
            base.setupNode();

            using (var dr =
                SqlHelper.ExecuteReader("select published, documentUser, coalesce(templateId, cmsDocumentType.templateNodeId) as templateId, text, releaseDate, expireDate, updateDate from cmsDocument inner join cmsContent on cmsDocument.nodeId = cmsContent.Nodeid left join cmsDocumentType on cmsDocumentType.contentTypeNodeId = cmsContent.contentType and cmsDocumentType.IsDefault = 1 where versionId = @versionId",
                                        SqlHelper.CreateParameter("@versionId", Version)))
            {
                if (dr.Read())
                {
                    _creator = User;
                    _writer = User.GetUser(dr.GetInt("documentUser"));

                    if (!dr.IsNull("templateId"))
                        _template = dr.GetInt("templateId");
                    if (!dr.IsNull("releaseDate"))
                        _release = dr.GetDateTime("releaseDate");
                    if (!dr.IsNull("expireDate"))
                        _expire = dr.GetDateTime("expireDate");
                    if (!dr.IsNull("updateDate"))
                        _updated = dr.GetDateTime("updateDate");
                }
                else
                {
                    throw new ArgumentException(string.Format("No Document exists with Version '{0}'", Version));
                }
            }
        }

        protected void InitializeDocument(User InitUser, User InitWriter, string InitText, int InitTemplate,
                                          DateTime InitReleaseDate, DateTime InitExpireDate, DateTime InitUpdateDate,
                                          bool InitPublished)
        {
            if (InitUser == null)
            {
                throw new ArgumentNullException("InitUser");
            }
            if (InitWriter == null)
            {
                throw new ArgumentNullException("InitWriter");
            }
            _creator = InitUser;
            _writer = InitWriter;
            SetText(InitText);
            _template = InitTemplate;
            _release = InitReleaseDate;
            _expire = InitExpireDate;
            _updated = InitUpdateDate;
            _hasPublishedVersion = InitPublished;
        }

        /// <summary>
        /// Updates this document object based on the data in the IRecordsReader for data returned from the SqlOptimizedMany SQL call
        /// </summary>
        /// <param name="dr"></param>
        protected void PopulateDocumentFromReader(IRecordsReader dr)
        {
            var hc = dr.GetInt("children") > 0;

            int? masterContentType = null;

            if (!dr.IsNull("masterContentType"))
                masterContentType = dr.GetInt("masterContentType");

            SetupDocumentForTree(dr.GetGuid("uniqueId")
                , dr.GetShort("level")
                , dr.GetInt("parentId")
                , dr.GetInt("nodeUser")
                , dr.GetInt("documentUser")
                , !dr.GetBoolean("trashed") && (dr.GetInt("isPublished") == 1) //set published... double check trashed property
                , dr.GetString("path")
                , dr.GetString("text")
                , dr.GetDateTime("createDate")
                , dr.GetDateTime("updateDate")
                , dr.GetDateTime("versionDate")
                , dr.GetString("icon")
                , hc
                , dr.GetString("alias")
                , dr.GetString("thumbnail")
                , dr.GetString("description")
                , masterContentType
                , dr.GetInt("contentTypeId")
                , dr.GetInt("templateId"));

            if (!dr.IsNull("releaseDate"))
                _release = dr.GetDateTime("releaseDate");
            if (!dr.IsNull("expireDate"))
                _expire = dr.GetDateTime("expireDate");

        }

        protected void SaveXmlPreview(XmlDocument xd)
        {
            SavePreviewXml(generateXmlWithoutSaving(xd), Version);
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Updates this the Published property for all pre-populated descendant nodes in list format
        /// </summary>
        /// <param name="descendantsList">The pre-populated list of descendants of the root node passed in</param>
        /// <param name="root">The very root document retreiving the ancestors</param>
        /// <remarks>
        /// This method will ensure that the document's Published is automatically set based on this (the root ancestor) document.
        /// It will set the Published based on the documents with the shortest path first since the parent document to those documents
        /// are 'this' document. Then we will go to the next level and set the Published based on their parent documents... since they will
        /// now have the Published property set. and so on.
        /// </remarks>
        private static void UpdatePublishedOnDescendants(List<Document> descendantsList, Document root)
        {
            //create a new list containing 'this' so the list becomes DescendantsAndSelf
            var descendantsAndSelf = descendantsList.Concat(new[] { root }).ToList();

            //determine all path lengths in the list
            var pathLengths = descendantsList.Select(x => x.Path.Split(',').Length).Distinct();
            //start with the shortest paths
            foreach (var pathLength in pathLengths.OrderBy(x => x))
            {
                var length = pathLength;
                var docsWithPathLength = descendantsList.Where(x => x.Path.Split(',').Length == length);                
                //iterate over all documents with the current path length
                foreach (var d in docsWithPathLength)
                {
                    //we need to find the current doc's parent doc in the descendantsOrSelf list
                    var parent = descendantsAndSelf.SingleOrDefault(x => x.Id == d.ParentId);
                    if (parent != null)
                    {
                        //we are published if our parent is published and we have a published version
                        d.Published = parent.Published && d.HasPublishedVersion();
                        
                        //our path is published if our parent is published
                        d.PathPublished = parent.Published;
                    }
                }
            }

            
        }

        /// <summary>
        /// Sets properties on this object based on the parameters
        /// </summary>
        /// <param name="uniqueId"></param>
        /// <param name="level"></param>
        /// <param name="parentId"></param>
        /// <param name="creator"></param>
        /// <param name="writer"></param>
        /// <param name="hasPublishedVersion">If this document has a published version</param>
        /// <param name="path"></param>
        /// <param name="text"></param>
        /// <param name="createDate"></param>
        /// <param name="updateDate"></param>
        /// <param name="versionDate"></param>
        /// <param name="icon"></param>
        /// <param name="hasChildren"></param>
        /// <param name="contentTypeAlias"></param>
        /// <param name="contentTypeThumb"></param>
        /// <param name="contentTypeDesc"></param>
        /// <param name="masterContentType"></param>
        /// <param name="contentTypeId"></param>
        /// <param name="templateId"></param>
        private void SetupDocumentForTree(Guid uniqueId, int level, int parentId, int creator, int writer, bool hasPublishedVersion, string path,
                                         string text, DateTime createDate, DateTime updateDate,
                                         DateTime versionDate, string icon, bool hasChildren, string contentTypeAlias, string contentTypeThumb,
                                           string contentTypeDesc, int? masterContentType, int contentTypeId, int templateId)
        {
            SetupNodeForTree(uniqueId, _objectType, level, parentId, creator, path, text, createDate, hasChildren);

            _writerId = writer;
            _hasPublishedVersion = hasPublishedVersion;
            _updated = updateDate;
            _template = templateId;
            ContentType = new ContentType(contentTypeId, contentTypeAlias, icon, contentTypeThumb, masterContentType);
            ContentTypeIcon = icon;
            VersionDate = versionDate;
        }

        private XmlAttribute addAttribute(XmlDocument Xd, string Name, string Value)
        {
            XmlAttribute temp = Xd.CreateAttribute(Name);
            temp.Value = Value;
            return temp;
        }

        /// <summary>
        /// This needs to be synchronized since we're doing multiple sql operations in the single method
        /// </summary>
        /// <param name="x"></param>
        [MethodImpl(MethodImplOptions.Synchronized)]
        private void saveXml(XmlNode x)
        {
            bool exists = (SqlHelper.ExecuteScalar<int>("SELECT COUNT(nodeId) FROM cmsContentXml WHERE nodeId=@nodeId",
                                            SqlHelper.CreateParameter("@nodeId", Id)) != 0);
            string sql = exists ? "UPDATE cmsContentXml SET xml = @xml WHERE nodeId=@nodeId"
                                : "INSERT INTO cmsContentXml(nodeId, xml) VALUES (@nodeId, @xml)";
            SqlHelper.ExecuteNonQuery(sql,
                                      SqlHelper.CreateParameter("@nodeId", Id),
                                      SqlHelper.CreateParameter("@xml", x.OuterXml));
        }

        private XmlNode importXml()
        {
            XmlDocument xmlDoc = new XmlDocument();
            XmlReader xmlRdr = SqlHelper.ExecuteXmlReader(string.Format(
                                                       "select xml from cmsContentXml where nodeID = {0}", Id));
            xmlDoc.Load(xmlRdr);

            return xmlDoc.FirstChild;
        }

        /// <summary>
        /// Used internally to permanently delete the data from the database
        /// </summary>
        /// <returns>returns true if deletion isn't cancelled</returns>
        private bool DeletePermanently()
        {
            DeleteEventArgs e = new DeleteEventArgs();

            FireBeforeDelete(e);

            if (!e.Cancel)
            {
                foreach (Document d in Children.ToList())
                {
                    d.DeletePermanently();
                }

                umbraco.BusinessLogic.Actions.Action.RunActionHandlers(this, ActionDelete.Instance);

                // Remove all files
                DeleteAssociatedMediaFiles();

                //remove any domains associated
                var domains = Domain.GetDomainsById(this.Id).ToList();
                domains.ForEach(x => x.Delete());

                SqlHelper.ExecuteNonQuery("delete from cmsDocument where NodeId = " + Id);
                base.delete();

                FireAfterDelete(e);
            }
            return !e.Cancel;
        }

        /// <summary>
        /// Used internally to move the node to the recyle bin
        /// </summary>
        /// <returns>Returns true if the move was not cancelled</returns>
        private bool MoveToTrash()
        {
            MoveToTrashEventArgs e = new MoveToTrashEventArgs();
            FireBeforeMoveToTrash(e);

            if (!e.Cancel)
            {
                umbraco.BusinessLogic.Actions.Action.RunActionHandlers(this, ActionDelete.Instance);
                UnPublish();
                Move((int)RecycleBin.RecycleBinType.Content);
                FireAfterMoveToTrash(e);
            }
            return !e.Cancel;
        }

        #endregion

        #region Events

        /// <summary>
        /// The save event handler
        /// </summary>
        public delegate void SaveEventHandler(Document sender, SaveEventArgs e);
        /// <summary>
        /// The New event handler
        /// </summary>
        public delegate void NewEventHandler(Document sender, NewEventArgs e);
        /// <summary>
        /// The delete  event handler
        /// </summary>
        public delegate void DeleteEventHandler(Document sender, DeleteEventArgs e);
        /// <summary>
        /// The publish event handler
        /// </summary>
        public delegate void PublishEventHandler(Document sender, PublishEventArgs e);
        /// <summary>
        /// The Send To Publish event handler
        /// </summary>
        public delegate void SendToPublishEventHandler(Document sender, SendToPublishEventArgs e);
        /// <summary>
        /// The unpublish event handler
        /// </summary>
        public delegate void UnPublishEventHandler(Document sender, UnPublishEventArgs e);
        /// <summary>
        /// The copy event handler
        /// </summary>
        public delegate void CopyEventHandler(Document sender, CopyEventArgs e);
        /// <summary>
        /// The rollback event handler
        /// </summary>
        public delegate void RollBackEventHandler(Document sender, RollBackEventArgs e);

        /// <summary>
        /// The Move to trash event handler
        /// </summary>
        public delegate void MoveToTrashEventHandler(Document sender, MoveToTrashEventArgs e);

        /// <summary>
        /// Occurs when [before save].
        /// </summary>
        public static event SaveEventHandler BeforeSave;
        /// <summary>
        /// Raises the <see cref="E:BeforeSave"/> event.
        /// </summary>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        protected internal new virtual void FireBeforeSave(SaveEventArgs e)
        {
            if (BeforeSave != null)
            {
                BeforeSave(this, e);
            }
        }

        /// <summary>
        /// Occurs when [after save].
        /// </summary>
        public static event SaveEventHandler AfterSave;
        /// <summary>
        /// Raises the <see cref="E:AfterSave"/> event.
        /// </summary>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        protected virtual void FireAfterSave(SaveEventArgs e)
        {
            if (AfterSave != null)
            {
                AfterSave(this, e);
            }
        }

        /// <summary>
        /// Occurs when [new].
        /// </summary>
        public static event NewEventHandler New;
        /// <summary>
        /// Raises the <see cref="E:New"/> event.
        /// </summary>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        protected virtual void OnNew(NewEventArgs e)
        {
            if (New != null)
                New(this, e);
        }

        //TODO: Slace - Document this
        public static event EventHandler<DocumentNewingEventArgs> Newing;
        protected static void OnNewing(DocumentNewingEventArgs e)
        {
            if (Newing != null)
            {
                Newing(null, e);
            }
        }

        /// <summary>
        /// Occurs when [before delete].
        /// </summary>
        public new static event DeleteEventHandler BeforeDelete;

        /// <summary>
        /// Raises the <see cref="E:BeforeDelete"/> event.
        /// </summary>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        protected new virtual void FireBeforeDelete(DeleteEventArgs e)
        {
            if (BeforeDelete != null)
                BeforeDelete(this, e);
        }

        /// <summary>
        /// Occurs when [after delete].
        /// </summary>
        public new static event DeleteEventHandler AfterDelete;

        /// <summary>
        /// Raises the <see cref="E:AfterDelete"/> event.
        /// </summary>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        protected new virtual void FireAfterDelete(DeleteEventArgs e)
        {
            if (AfterDelete != null)
                AfterDelete(this, e);
        }


        /// <summary>
        /// Occurs when [before delete].
        /// </summary>
        public static event MoveToTrashEventHandler BeforeMoveToTrash;
        /// <summary>
        /// Raises the <see cref="E:BeforeDelete"/> event.
        /// </summary>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        protected virtual void FireBeforeMoveToTrash(MoveToTrashEventArgs e)
        {
            if (BeforeMoveToTrash != null)
                BeforeMoveToTrash(this, e);
        }


        /// <summary>
        /// Occurs when [after move to trash].
        /// </summary>
        public static event MoveToTrashEventHandler AfterMoveToTrash;
        /// <summary>
        /// Fires the after move to trash.
        /// </summary>
        /// <param name="e">The <see cref="umbraco.cms.businesslogic.MoveToTrashEventArgs"/> instance containing the event data.</param>
        protected virtual void FireAfterMoveToTrash(MoveToTrashEventArgs e)
        {
            if (AfterMoveToTrash != null)
                AfterMoveToTrash(this, e);
        }

        /// <summary>
        /// Occurs when [before publish].
        /// </summary>
        public static event PublishEventHandler BeforePublish;
        /// <summary>
        /// Raises the <see cref="E:BeforePublish"/> event.
        /// </summary>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        protected virtual void FireBeforePublish(PublishEventArgs e)
        {
            if (BeforePublish != null)
                BeforePublish(this, e);
        }

        /// <summary>
        /// Occurs when [after publish].
        /// </summary>
        public static event PublishEventHandler AfterPublish;
        /// <summary>
        /// Raises the <see cref="E:AfterPublish"/> event.
        /// </summary>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        protected virtual void FireAfterPublish(PublishEventArgs e)
        {
            if (AfterPublish != null)
                AfterPublish(this, e);
        }
        /// <summary>
        /// Occurs when [before publish].
        /// </summary>
        public static event SendToPublishEventHandler BeforeSendToPublish;
        /// <summary>
        /// Raises the <see cref="E:BeforePublish"/> event.
        /// </summary>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        protected virtual void FireBeforeSendToPublish(SendToPublishEventArgs e)
        {
            if (BeforeSendToPublish != null)
                BeforeSendToPublish(this, e);
        }


        /// <summary>
        /// Occurs when [after publish].
        /// </summary>
        public static event SendToPublishEventHandler AfterSendToPublish;
        /// <summary>
        /// Raises the <see cref="E:AfterPublish"/> event.
        /// </summary>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        protected virtual void FireAfterSendToPublish(SendToPublishEventArgs e)
        {
            if (AfterSendToPublish != null)
                AfterSendToPublish(this, e);
        }

        /// <summary>
        /// Occurs when [before un publish].
        /// </summary>
        public static event UnPublishEventHandler BeforeUnPublish;
        /// <summary>
        /// Raises the <see cref="E:BeforeUnPublish"/> event.
        /// </summary>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        protected virtual void FireBeforeUnPublish(UnPublishEventArgs e)
        {
            if (BeforeUnPublish != null)
                BeforeUnPublish(this, e);
        }

        /// <summary>
        /// Occurs when [after un publish].
        /// </summary>
        public static event UnPublishEventHandler AfterUnPublish;
        /// <summary>
        /// Raises the <see cref="E:AfterUnPublish"/> event.
        /// </summary>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        protected virtual void FireAfterUnPublish(UnPublishEventArgs e)
        {
            if (AfterUnPublish != null)
                AfterUnPublish(this, e);
        }

        /// <summary>
        /// Occurs when [before copy].
        /// </summary>
        public static event CopyEventHandler BeforeCopy;
        /// <summary>
        /// Raises the <see cref="E:BeforeCopy"/> event.
        /// </summary>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        protected virtual void FireBeforeCopy(CopyEventArgs e)
        {
            if (BeforeCopy != null)
                BeforeCopy(this, e);
        }

        /// <summary>
        /// Occurs when [after copy].
        /// </summary>
        public static event CopyEventHandler AfterCopy;
        /// <summary>
        /// Raises the <see cref="E:AfterCopy"/> event.
        /// </summary>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        protected virtual void FireAfterCopy(CopyEventArgs e)
        {
            if (AfterCopy != null)
                AfterCopy(this, e);
        }

        /// <summary>
        /// Occurs when [before roll back].
        /// </summary>
        public static event RollBackEventHandler BeforeRollBack;
        /// <summary>
        /// Raises the <see cref="E:BeforeRollBack"/> event.
        /// </summary>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        protected virtual void FireBeforeRollBack(RollBackEventArgs e)
        {
            if (BeforeRollBack != null)
                BeforeRollBack(this, e);
        }

        /// <summary>
        /// Occurs when [after roll back].
        /// </summary>
        public static event RollBackEventHandler AfterRollBack;
        /// <summary>
        /// Raises the <see cref="E:AfterRollBack"/> event.
        /// </summary>
        /// <param name="e">The <see cref="System.EventArgs"/> instance containing the event data.</param>
        protected virtual void FireAfterRollBack(RollBackEventArgs e)
        {
            if (AfterRollBack != null)
                AfterRollBack(this, e);
        }
        #endregion


    }
}
