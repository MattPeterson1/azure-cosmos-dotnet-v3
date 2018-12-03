﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.SDK.EmulatorTests
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Collections;
    using Microsoft.Azure.Cosmos.Internal;
    using Microsoft.Azure.Cosmos.Routing;
    using Microsoft.Azure.Cosmos.Utils;
    using Microsoft.Azure.Documents.Services.Management.Tests;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using Newtonsoft.Json;

    internal static class TestCommon
    {
        public const int MinimumOfferThroughputToCreateElasticCollectionInTests = 10100;
        public const int CollectionQuotaForDatabaseAccountForQuotaCheckTests = 2;
        public const int NumberOfPartitionsPerCollectionInLocalEmulatorTest = 5;
        public const int CollectionQuotaForDatabaseAccountInTests = 16;
        public const int CollectionPartitionQuotaForDatabaseAccountInTests = 100;
        public const int TimeinMSTakenByTheMxQuotaConfigUpdateToRefreshInTheBackEnd = 240000; // 240 seconds
        public const int Gen3MaxCollectionCount = 16;
        public const int Gen3MaxCollectionSizeInKB = 256 * 1024;
        public const int MaxCollectionSizeInKBWithRuntimeServiceBindingEnabled = 1024 * 1024;
        public const int ReplicationFactor = 3;
        public static readonly int TimeToWaitForOperationCommitInSec = 2;

        private static readonly int serverStalenessIntervalInSeconds;
        private static readonly int masterStalenessIntervalInSeconds;

        static TestCommon()
        {
            TestCommon.serverStalenessIntervalInSeconds = int.Parse(ConfigurationManager.AppSettings["ServerStalenessIntervalInSeconds"], CultureInfo.InvariantCulture);
            TestCommon.masterStalenessIntervalInSeconds = int.Parse(ConfigurationManager.AppSettings["MasterStalenessIntervalInSeconds"], CultureInfo.InvariantCulture);
        }

        internal static CosmosConfiguration GetDefaultConfiguration()
        {
            string authKey = ConfigurationManager.AppSettings["MasterKey"];
            string endpoint = ConfigurationManager.AppSettings["GatewayEndpoint"];

            return new CosmosConfiguration(accountEndPoint: endpoint, accountKey: authKey);
        }

        internal static CosmosClient CreateCosmosClient(CosmosConfiguration cosmosConfiguration = null)
        {
            if(cosmosConfiguration == null)
            {
                cosmosConfiguration = GetDefaultConfiguration();
            }

            return new CosmosClient(cosmosConfiguration);
        }

        internal static DocumentClient CreateClient(bool useGateway, Protocol protocol = Protocol.Tcp,
            int timeoutInSeconds = 10,
            ConsistencyLevel? defaultConsistencyLevel = null,
            AuthorizationTokenType tokenType = AuthorizationTokenType.PrimaryMasterKey,
            bool createForGeoRegion = false,
            bool enableEndpointDiscovery = true,
            bool? enableReadRequestFallback = null,
            List<string> preferredLocations = null,
            RetryOptions retryOptions = null)
        {
            string authKey = ConfigurationManager.AppSettings["MasterKey"];

            // The Public emulator has no other keys
            //switch (tokenType)
            //{
            //    case AuthorizationTokenType.PrimaryMasterKey:
            //        authKey = ConfigurationManager.AppSettings["MasterKey"];
            //        break;

            //    case AuthorizationTokenType.SystemReadOnly:
            //        authKey = ConfigurationManager.AppSettings["ReadOnlySystemKey"];
            //        break;

            //    case AuthorizationTokenType.SystemReadWrite:
            //        authKey = ConfigurationManager.AppSettings["ReadWriteSystemKey"];
            //        break;

            //    case AuthorizationTokenType.SystemAll:
            //        authKey = ConfigurationManager.AppSettings["FullSystemKey"];
            //        break;
            //    case AuthorizationTokenType.PrimaryReadonlyMasterKey:
            //        authKey = ConfigurationManager.AppSettings["primaryReadonlyMasterKey"];
            //        break;
            //    default:
            //        throw new ArgumentException("tokenType");
            //}

            ConnectionPolicy connectionPolicy = null;
#if DIRECT_MODE
            // DIRECT MODE has ReadFeed issues in the Public emulator
            if (useGateway)
            {
                connectionPolicy = new ConnectionPolicy
                {
                    ConnectionMode = ConnectionMode.Gateway,
                    EnableEndpointDiscovery = enableEndpointDiscovery,
                };
            }
            else
            {
                connectionPolicy = new ConnectionPolicy
                {
                    ConnectionMode = ConnectionMode.Direct,
                    ConnectionProtocol = protocol,
                    RequestTimeout = TimeSpan.FromSeconds(timeoutInSeconds),
                    EnableEndpointDiscovery = enableEndpointDiscovery,
                    EnableReadRequestsFallback = enableReadRequestFallback
                };
            }
#endif
#if !DIRECT_MODE
            connectionPolicy = new ConnectionPolicy
            {
                ConnectionMode = ConnectionMode.Gateway,
                EnableEndpointDiscovery = enableEndpointDiscovery,
            };
#endif
            if (retryOptions != null)
            {
                connectionPolicy.RetryOptions = retryOptions;
            }

            if (preferredLocations != null)
            {
                foreach (string preferredLocation in preferredLocations)
                {
                    connectionPolicy.PreferredLocations.Add(preferredLocation);
                }
            }

            Uri uri = new Uri(ConfigurationManager.AppSettings["GatewayEndpoint"]);

            DocumentClient client = new DocumentClient(
                uri,
                authKey,
                new JsonSerializerSettings()
                {
                    ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor
                },
                connectionPolicy,
                defaultConsistencyLevel);

            return client;
        }

        internal static DocumentClient CreateNonSslTestClient(string key, int timeoutInSeconds = 10)
        {
            return new DocumentClient(
                new Uri(ConfigurationManager.AppSettings["GatewayEndpoint"]),
                key,
                new ConnectionPolicy
                {
                    ConnectionMode = ConnectionMode.Direct,
                    ConnectionProtocol = Protocol.Tcp,
                    RequestTimeout = TimeSpan.FromSeconds(timeoutInSeconds)
                });
        }

        internal static void AddMasterAuthorizationHeader(this HttpClient client, string verb,
            string resourceId,
            string resourceType,
            INameValueCollection headers,
            string key)
        {
            if (String.IsNullOrEmpty(verb)) throw new ArgumentException("verb");
            if (String.IsNullOrEmpty(key)) throw new ArgumentException("key");
            if (headers == null) throw new ArgumentNullException("headers");

            string xDate = DateTime.UtcNow.ToString("r");

            client.DefaultRequestHeaders.Remove(HttpConstants.HttpHeaders.XDate);
            client.DefaultRequestHeaders.Add(HttpConstants.HttpHeaders.XDate, xDate);

            headers.Remove(HttpConstants.HttpHeaders.XDate);
            headers.Add(HttpConstants.HttpHeaders.XDate, xDate);

            client.DefaultRequestHeaders.Remove(HttpConstants.HttpHeaders.Authorization);
            client.DefaultRequestHeaders.Add(HttpConstants.HttpHeaders.Authorization,
                AuthorizationHelper.GenerateKeyAuthorizationSignature(verb, resourceId, resourceType, headers, key));
        }

        internal static IList<T> ListAll<T>(DocumentClient client,
            string resourceIdOrFullName,
            INameValueCollection headers = null,
            bool readWithRetry = false) where T : CosmosResource, new()
        {
            List<T> result = new List<T>();

            INameValueCollection localHeaders = null;
            if (headers != null)
            {
                localHeaders = new StringKeyValueCollection(headers);
            }
            else
            {
                localHeaders = new StringKeyValueCollection();
            }

            string continuationToken = null;
            FeedResponse<T> pagedResult = null;
            do
            {
                if (!string.IsNullOrEmpty(continuationToken))
                {
                    localHeaders[HttpConstants.HttpHeaders.Continuation] = continuationToken;
                }

                if (readWithRetry)
                {
                    pagedResult = client.ReadFeedWithRetry<T>(resourceIdOrFullName,
                        localHeaders);
                }
                else
                {
                    pagedResult = client.ReadFeed<T>(resourceIdOrFullName,
                        localHeaders);
                }

                if (typeof(T) == typeof(Document))
                {
                    foreach (T entry in pagedResult)
                    {
                        Document document = (Document)(object)entry;
                        result.Add(entry);
                    }
                }
                else
                {
                    result.AddRange(pagedResult);
                }
                continuationToken = pagedResult.ResponseContinuation;
            } while (!string.IsNullOrEmpty(pagedResult.ResponseContinuation));

            return result;
        }

        private static DocumentServiceRequest CreateRequest(
            OperationType operationType,
            string resourceIdOrFullName,
            ResourceType resourceType,
            INameValueCollection headers,
            AuthorizationTokenType authTokenType)
        {
            if (PathsHelper.IsNameBased(resourceIdOrFullName))
            {
                return DocumentServiceRequest.CreateFromName(
                    operationType,
                    resourceIdOrFullName,
                    resourceType,
                    authTokenType,
                    headers);
            }
            else
            {
                return DocumentServiceRequest.Create(
                    operationType,
                    resourceIdOrFullName,
                    resourceType,
                    authTokenType,
                    headers);
            }
        }

        internal static void RouteToTheOnlyPartition(DocumentClient client, DocumentServiceRequest request)
        {
            ClientCollectionCache collectionCache = client.GetCollectionCacheAsync().Result;
            CosmosContainerSettings collection = collectionCache.ResolveCollectionAsync(request, CancellationToken.None).Result;
            IRoutingMapProvider routingMapProvider = client.GetPartitionKeyRangeCacheAsync().Result;
            IReadOnlyList<PartitionKeyRange> ranges = routingMapProvider.TryGetOverlappingRangesAsync(
                collection.ResourceId,
                Range<string>.GetPointRange(PartitionKeyInternal.MinimumInclusiveEffectivePartitionKey)).Result;
            request.RouteTo(new PartitionKeyRangeIdentity(collection.ResourceId, ranges.Single().Id));
        }

        // todo: elasticcollections remove this when scripts are created directly in server again.
        // For now we need it for some low level tests which need direct access.
        internal static ResourceResponse<T> CreateScriptDirect<T>(DocumentClient client, string collectionLink, T resource) where T : CosmosResource, new()
        {
            using (DocumentServiceRequest request = DocumentServiceRequest.Create(
                 OperationType.Create,
                 collectionLink,
                 resource,
                 TestCommon.ToResourceType(typeof(T)),
                 AuthorizationTokenType.PrimaryMasterKey,
                 null))
            {
                string authorization = ((IAuthorizationTokenProvider)client).GetUserAuthorizationToken(request.ResourceAddress,
                    PathsHelper.GetResourcePath(request.ResourceType),
                    HttpConstants.HttpMethods.Post, request.Headers, AuthorizationTokenType.PrimaryMasterKey);
                request.Headers[HttpConstants.HttpHeaders.Authorization] = authorization;

                RouteToTheOnlyPartition(client, request);

                using (new ActivityScope(Guid.NewGuid()))
                {
                    DocumentServiceResponse response = client.StoreModel.ProcessMessageAsync(request).Result;
                    return new ResourceResponse<T>(response);
                }
            }
        }

        // todo: elasticcollections remove this when scripts are created directly in server again.
        // For now we need it for some low level tests which need direct access.
        internal static ResourceResponse<T> UpdateScriptDirect<T>(DocumentClient client, T resource) where T : CosmosResource, new()
        {
            using (DocumentServiceRequest request = DocumentServiceRequest.Create(
                 OperationType.Replace,
                 resource,
                 TestCommon.ToResourceType(typeof(T)),
                 AuthorizationTokenType.PrimaryMasterKey,
                 null,
                 null))
            {
                string authorization = ((IAuthorizationTokenProvider)client).GetUserAuthorizationToken(request.ResourceAddress,
                    PathsHelper.GetResourcePath(request.ResourceType),
                    HttpConstants.HttpMethods.Put, request.Headers, AuthorizationTokenType.PrimaryMasterKey);
                request.Headers[HttpConstants.HttpHeaders.Authorization] = authorization;
                RouteToTheOnlyPartition(client, request);

                using (new ActivityScope(Guid.NewGuid()))
                {
                    DocumentServiceResponse response = client.StoreModel.ProcessMessageAsync(request).Result;
                    return new ResourceResponse<T>(response);
                }
            }
        }

        // todo: elasticcollections remove this when scripts are created directly in server again.
        // For now we need it for some low level tests which need direct access.
        internal static ResourceResponse<T> DeleteScriptDirect<T>(DocumentClient client, string resourceIdOrFullName) where T : CosmosResource, new()
        {
            using (DocumentServiceRequest request = CreateRequest(
                           OperationType.Delete,
                           resourceIdOrFullName,
                           TestCommon.ToResourceType(typeof(T)),
                           null,
                           AuthorizationTokenType.PrimaryMasterKey))
            {
                string authorization = ((IAuthorizationTokenProvider)client).GetUserAuthorizationToken(request.ResourceAddress,
                    PathsHelper.GetResourcePath(request.ResourceType),
                    HttpConstants.HttpMethods.Delete, request.Headers, AuthorizationTokenType.PrimaryMasterKey);
                request.Headers[HttpConstants.HttpHeaders.Authorization] = authorization;
                RouteToTheOnlyPartition(client, request);

                using (new ActivityScope(Guid.NewGuid()))
                {
                    DocumentServiceResponse response = client.StoreModel.ProcessMessageAsync(request).Result;
                    return new ResourceResponse<T>(response);
                }
            }
        }

        // todo: elasticcollections remove this when scripts are created directly in server again.
        // For now we need it for some low level tests which need direct access.
        internal static IList<T> ListAllScriptDirect<T>(DocumentClient client,
                string resourceIdOrFullName,
                INameValueCollection headers = null) where T : CosmosResource, new()
        {
            List<T> result = new List<T>();

            INameValueCollection localHeaders = null;
            if (headers != null)
            {
                localHeaders = new StringKeyValueCollection(headers);
            }
            else
            {
                localHeaders = new StringKeyValueCollection();
            }

            string continuationToken = null;
            FeedResponse<T> pagedResult = null;
            do
            {
                if (!string.IsNullOrEmpty(continuationToken))
                {
                    localHeaders[HttpConstants.HttpHeaders.Continuation] = continuationToken;
                }

                int nMaxRetry = 5;

                do
                {
                    try
                    {
                        pagedResult = ReadScriptFeedDirect<T>(client, resourceIdOrFullName, localHeaders);
                        break;
                    }
                    catch (ServiceUnavailableException)
                    {
                        if (--nMaxRetry > 0)
                        {
                            Task.Delay(5000); //Wait 5 seconds before retry.
                        }
                        else
                        {
                            Assert.Fail("Service is not available after 5 retries");
                        }
                    }
                    catch (GoneException)
                    {
                        if (--nMaxRetry > 0)
                        {
                            Task.Delay(5000); //Wait 5 seconds before retry.
                        }
                        else
                        {
                            Assert.Fail("Service is not available after 5 retries");
                        }
                    }
                } while (true);


                result.AddRange(pagedResult);
                continuationToken = pagedResult.ResponseContinuation;
            } while (!string.IsNullOrEmpty(pagedResult.ResponseContinuation));

            return result;
        }

        // todo: elasticcollections remove this when scripts are created directly in server again.
        // For now we need it for some low level tests which need direct access.
        private static FeedResponse<T> ReadScriptFeedDirect<T>(
            DocumentClient client,
            string resourceIdOrFullName,
            INameValueCollection localHeaders) where T : CosmosResource, new()
        {
            try
            {
                using (
                    DocumentServiceRequest request = CreateRequest(
                        OperationType.ReadFeed,
                        resourceIdOrFullName,
                        TestCommon.ToResourceType(typeof(T)),
                        localHeaders,
                        AuthorizationTokenType.PrimaryMasterKey))
                {
                    RouteToTheOnlyPartition(client, request);

                    string authorization = ((IAuthorizationTokenProvider)client).GetUserAuthorizationToken(
                        request.ResourceAddress,
                        PathsHelper.GetResourcePath(request.ResourceType),
                        HttpConstants.HttpMethods.Get,
                        request.Headers,
                        AuthorizationTokenType.PrimaryMasterKey);
                    request.Headers[HttpConstants.HttpHeaders.Authorization] = authorization;

                    using (new ActivityScope(Guid.NewGuid()))
                    {
                        DocumentServiceResponse response = client.StoreModel.ProcessMessageAsync(request).Result;

                        FeedResource<T> feedResource = response.GetResource<FeedResource<T>>();
                        return new FeedResponse<T>(feedResource, feedResource.Count, response.Headers);
                    }
                }
            }
            catch (AggregateException ex)
            {
                throw ex.InnerException;
            }
        }

        internal static CosmosDatabaseSettings CreateOrGetDatabase(DocumentClient client)
        {
            IList<CosmosDatabaseSettings> databases = TestCommon.ListAll<CosmosDatabaseSettings>(
                client,
                null);

            if (databases.Count == 0)
            {
                return TestCommon.CreateDatabase(client);
            }
            return databases[0];
        }

        internal static CosmosDatabaseSettings CreateDatabase(DocumentClient client, string databaseName = null)
        {
            string name = databaseName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = Guid.NewGuid().ToString("N");
            }
            CosmosDatabaseSettings database = new CosmosDatabaseSettings
            {
                Id = name,
            };
            return client.Create<CosmosDatabaseSettings>(null, database);
        }

        internal static User CreateOrGetUser(DocumentClient client)
        {
            CosmosDatabaseSettings ignored = null;
            return TestCommon.CreateOrGetUser(client, out ignored);
        }

        internal static User CreateOrGetUser(DocumentClient client, out CosmosDatabaseSettings database)
        {
            database = TestCommon.CreateOrGetDatabase(client);

            IList<User> users = TestCommon.ListAll<User>(
                client,
                database.ResourceId);

            if (users.Count == 0)
            {
                User user = new User
                {
                    Id = Guid.NewGuid().ToString("N")
                };
                return client.Create<User>(database.ResourceId, user);
            }
            return client.Read<User>(users[0].ResourceId);
        }

        internal static User UpsertUser(DocumentClient client, out CosmosDatabaseSettings database)
        {
            database = TestCommon.CreateOrGetDatabase(client);

            User user = new User
            {
                Id = Guid.NewGuid().ToString("N")
            };
            return client.Upsert<User>(database.GetIdOrFullName(), user);
        }

        internal static CosmosContainerSettings CreateOrGetDocumentCollection(DocumentClient client)
        {
            CosmosDatabaseSettings ignored = null;
            return TestCommon.CreateOrGetDocumentCollection(client, out ignored);
        }

        internal static CosmosContainerSettings CreateOrGetDocumentCollection(DocumentClient client, out CosmosDatabaseSettings database)
        {
            database = TestCommon.CreateOrGetDatabase(client);

            IList<CosmosContainerSettings> documentCollections = TestCommon.ListAll<CosmosContainerSettings>(
                client,
                database.ResourceId);

            if (documentCollections.Count == 0)
            {
                CosmosContainerSettings documentCollection1 = new CosmosContainerSettings
                {
                    Id = Guid.NewGuid().ToString("N")
                };

                return TestCommon.CreateCollectionAsync(client, database, documentCollection1,
                    new RequestOptions() { OfferThroughput = 10000 }).Result;
            }

            return client.Read<CosmosContainerSettings>(documentCollections[0].ResourceId);
        }

        internal static Document CreateOrGetDocument(DocumentClient client)
        {
            CosmosContainerSettings ignored1 = null;
            CosmosDatabaseSettings ignored2 = null;

            return TestCommon.CreateOrGetDocument(client, out ignored1, out ignored2);
        }

        internal static Document CreateOrGetDocument(DocumentClient client, out CosmosContainerSettings documentCollection, out CosmosDatabaseSettings database)
        {
            documentCollection = TestCommon.CreateOrGetDocumentCollection(client, out database);

            IList<Document> documents = TestCommon.ListAll<Document>(
                client,
                documentCollection.ResourceId);

            if (documents.Count == 0)
            {
                Document document1 = new Document
                {
                    Id = Guid.NewGuid().ToString("N")
                };
                Document document = client.Create<Document>(documentCollection.ResourceId, document1);
                TestCommon.WaitForServerReplication();
                return document;
            }
            else
            {
                return client.Read<Document>(documents[0].ResourceId);
            }
        }

        internal static Document UpsertDocument(DocumentClient client, out CosmosContainerSettings documentCollection, out CosmosDatabaseSettings database)
        {
            documentCollection = TestCommon.CreateOrGetDocumentCollection(client, out database);

            Document document = new Document
            {
                Id = Guid.NewGuid().ToString("N")
            };

            return client.Upsert<Document>(documentCollection.GetIdOrFullName(), document);
        }

        internal static async Task CreateDataSet(DocumentClient client, string dbName, string collName, int numberOfDocuments, int inputThroughputOffer)
        {
            Random random = new Random();
            CosmosDatabaseSettings database = TestCommon.CreateDatabase(client, dbName);
            CosmosContainerSettings coll = await TestCommon.CreateCollectionAsync(client,
                database,
                new CosmosContainerSettings
                {
                    Id = collName,
                    PartitionKey = new PartitionKeyDefinition
                    {
                        Paths = new Collection<string> { "/partitionKey" },
                        Kind = PartitionKind.Hash,
                    }
                },
                new RequestOptions { OfferThroughput = inputThroughputOffer });

            StringBuilder sb = new StringBuilder();
            List<Task<ResourceResponse<Document>>> taskList = new List<Task<ResourceResponse<Document>>>();
            for (int i = 0; i < numberOfDocuments / 100; i++)
            {

                for (int j = 0; j < 100; j++)
                {
                    sb.Append("{\"id\":\"documentId" + (100 * i + j));
                    sb.Append("\",\"partitionKey\":" + (100 * i + j));
                    for (int k = 1; k < 20; k++)
                    {
                        sb.Append(",\"field_" + k + "\":" + random.Next(100000));
                    }
                    sb.Append("}");
                    string a = sb.ToString();
                    Task<ResourceResponse<Document>> task = client.CreateDocumentAsync(coll.SelfLink, JsonConvert.DeserializeObject(sb.ToString()));
                    taskList.Add(task);
                    sb.Clear();
                }

                while (taskList.Count > 0)
                {
                    Task<ResourceResponse<Document>> firstFinishedTask = await Task.WhenAny(taskList);
                    await firstFinishedTask;
                    taskList.Remove(firstFinishedTask);
                }
            }
        }

        public static void WaitForMasterReplication()
        {
            if (TestCommon.masterStalenessIntervalInSeconds != 0)
            {
                Task.Delay(TestCommon.masterStalenessIntervalInSeconds * 1000);
            }
        }

        public static void WaitForServerReplication()
        {
            if (TestCommon.serverStalenessIntervalInSeconds != 0)
            {
                Task.Delay(TestCommon.serverStalenessIntervalInSeconds * 1000);
            }
        }
        public static FeedResponse<T> ReadFeedWithRetry<T>(this DocumentClient client, string resourceIdOrFullName, INameValueCollection headers = null) where T : CosmosResource, new()
        {
            INameValueCollection ignored;
            return client.ReadFeedWithRetry<T>(resourceIdOrFullName, out ignored, headers);
        }

        public static FeedResponse<T> ReadFeedWithRetry<T>(this DocumentClient client, string resourceIdOrFullName, out INameValueCollection responseHeaders, INameValueCollection headers = null) where T : CosmosResource, new()
        {
            int nMaxRetry = 5;

            do
            {
                try
                {
                    return client.ReadFeed<T>(resourceIdOrFullName, out responseHeaders, headers);
                }
                catch (InternalServerErrorException)
                {
                    if (--nMaxRetry > 0)
                    {
                        Task.Delay(5000); //Wait 5 seconds before retry.                     
                    }
                    else
                    {
                        Assert.Fail("Service is not available after 5 retries");
                    }
                }
                catch (ServiceUnavailableException)
                {
                    if (--nMaxRetry > 0)
                    {
                        Task.Delay(5000); //Wait 5 seconds before retry.
                    }
                    else
                    {
                        Assert.Fail("Service is not available after 5 retries");
                    }
                }
                catch (RequestTimeoutException)
                {
                    if (--nMaxRetry > 0)
                    {
                        Task.Delay(5000); //Wait 5 seconds before retry.
                    }
                    else
                    {
                        Assert.Fail("Service is not available after 5 retries");
                    }
                }
                catch (GoneException)
                {
                    if (--nMaxRetry > 0)
                    {
                        Task.Delay(5000); //Wait 5 seconds before retry.
                    }
                    else
                    {
                        Assert.Fail("Service is not available after 5 retries");
                    }
                }
            } while (true);
        }

        //Sync helpers for DocumentStoreClient
        public static T Read<T>(this DocumentClient client, string resourceIdOrFullName, INameValueCollection headers = null) where T : CosmosResource, new()
        {
            INameValueCollection ignored;
            return client.Read<T>(resourceIdOrFullName, out ignored, headers);
        }

        public static T Read<T>(this DocumentClient client, string resourceIdOrFullName, out INameValueCollection responseHeaders, INameValueCollection headers = null) where T : CosmosResource, new()
        {
            try
            {
                DocumentServiceRequest request;
                if (PathsHelper.IsNameBased(resourceIdOrFullName))
                {
                    request = DocumentServiceRequest.CreateFromName(
                        OperationType.Read,
                        resourceIdOrFullName,
                        TestCommon.ToResourceType(typeof(T)),
                        AuthorizationTokenType.PrimaryMasterKey,
                        headers);
                }
                else
                {
                    request = DocumentServiceRequest.Create(
                        OperationType.Read,
                        resourceIdOrFullName,
                        TestCommon.ToResourceType(typeof(T)),
                        AuthorizationTokenType.PrimaryMasterKey,
                        headers);
                }

                if (request.ResourceType.IsPartitioned())
                {
                    request.Headers[HttpConstants.HttpHeaders.PartitionKey] = PartitionKeyInternal.Empty.ToJsonString();
                }

                DocumentServiceResponse response = client.ReadAsync(request).Result;
                responseHeaders = response.Headers;
                return response.GetResource<T>();
            }
            catch (AggregateException aggregatedException)
            {
                throw aggregatedException.InnerException;
            }
        }

        public static T Read<T>(this DocumentClient client, T resource, out INameValueCollection responseHeaders, INameValueCollection headers = null) where T : CosmosResource, new()
        {
            try
            {
                DocumentServiceRequest request;
                if (PathsHelper.IsNameBased(resource.ResourceId))
                {
                    request = DocumentServiceRequest.CreateFromName(
                        OperationType.Read,
                        resource.ResourceId,
                        TestCommon.ToResourceType(typeof(T)),
                        AuthorizationTokenType.PrimaryMasterKey,
                        headers);
                }
                else
                {
                    request = DocumentServiceRequest.Create(
                        OperationType.Read,
                        resource.ResourceId,
                        TestCommon.ToResourceType(typeof(T)),
                        AuthorizationTokenType.PrimaryMasterKey,
                        headers);
                }

                if (request.ResourceType.IsPartitioned())
                {
                    request.Headers[HttpConstants.HttpHeaders.PartitionKey] = resource.Id;//PartitionKeyInternal.Empty.ToJsonString();
                }

                DocumentServiceResponse response = client.ReadAsync(request).Result;
                responseHeaders = response.Headers;
                return response.GetResource<T>();
            }
            catch (AggregateException aggregatedException)
            {
                throw aggregatedException.InnerException;
            }
        }

        public static T ReadWithRetry<T>(this DocumentClient client, string resourceIdOrFullName, out INameValueCollection responseHeaders, INameValueCollection headers = null) where T : CosmosResource, new()
        {
            int nMaxRetry = 5;

            do
            {
                try
                {
                    return client.Read<T>(resourceIdOrFullName, out responseHeaders, headers);
                }
                catch (ServiceUnavailableException)
                {
                    if (--nMaxRetry > 0)
                    {
                        Task.Delay(5000); //Wait 5 seconds before retry.
                    }
                    else
                    {
                        Assert.Fail("Service is not available after 5 retries");
                    }
                }
                catch (GoneException)
                {
                    if (--nMaxRetry > 0)
                    {
                        Task.Delay(5000); //Wait 5 seconds before retry.
                    }
                    else
                    {
                        Assert.Fail("Service is not available after 5 retries");
                    }
                }
            } while (true);
        }

        public static T ReadWithRetry<T>(this DocumentClient client, T resource, out INameValueCollection responseHeaders, INameValueCollection headers = null) where T : CosmosResource, new()
        {
            int nMaxRetry = 5;

            do
            {
                try
                {
                    return client.Read<T>(resource, out responseHeaders, headers);
                }
                catch (ServiceUnavailableException)
                {
                    if (--nMaxRetry > 0)
                    {
                        Task.Delay(5000); //Wait 5 seconds before retry.
                    }
                    else
                    {
                        Assert.Fail("Service is not available after 5 retries");
                    }
                }
                catch (GoneException)
                {
                    if (--nMaxRetry > 0)
                    {
                        Task.Delay(5000); //Wait 5 seconds before retry.
                    }
                    else
                    {
                        Assert.Fail("Service is not available after 5 retries");
                    }
                }
            } while (true);
        }

        public static T Create<T>(this DocumentClient client, string resourceIdOrFullName, T resource, INameValueCollection headers = null) where T : CosmosResource, new()
        {
            try
            {
                DocumentServiceRequest request;

                if (PathsHelper.IsNameBased(resourceIdOrFullName))
                {
                    request = DocumentServiceRequest.CreateFromName(
                        OperationType.Create,
                        resource,
                        TestCommon.ToResourceType(typeof(T)),
                        headers,
                        resourceIdOrFullName,
                        AuthorizationTokenType.PrimaryMasterKey);
                }
                else
                {
                    request = DocumentServiceRequest.Create(
                        OperationType.Create,
                        resource,
                        TestCommon.ToResourceType(typeof(T)),
                        AuthorizationTokenType.PrimaryMasterKey,
                        headers,
                        resourceIdOrFullName);
                }

                if (request.ResourceType.IsPartitioned())
                {
                    request.Headers[HttpConstants.HttpHeaders.PartitionKey] = PartitionKeyInternal.Empty.ToJsonString();
                }

                return client.CreateAsync(request).Result.GetResource<T>();
            }
            catch (AggregateException aggregatedException)
            {
                throw aggregatedException.InnerException;
            }
        }

        public static T Upsert<T>(this DocumentClient client, string resourceIdOrFullName, T resource, INameValueCollection headers = null) where T : CosmosResource, new()
        {
            try
            {
                DocumentServiceRequest request;

                if (PathsHelper.IsNameBased(resourceIdOrFullName))
                {
                    request = DocumentServiceRequest.CreateFromName(
                        OperationType.Create,
                        resource,
                        TestCommon.ToResourceType(typeof(T)),
                        headers,
                        resourceIdOrFullName,
                        AuthorizationTokenType.PrimaryMasterKey);
                }
                else
                {
                    request = DocumentServiceRequest.Create(
                        OperationType.Create,
                        resource,
                        TestCommon.ToResourceType(typeof(T)),
                        AuthorizationTokenType.PrimaryMasterKey,
                        headers,
                        resourceIdOrFullName);
                }

                if (request.ResourceType.IsPartitioned())
                {
                    request.Headers[HttpConstants.HttpHeaders.PartitionKey] = PartitionKeyInternal.Empty.ToJsonString();
                }

                return client.UpsertAsync(request).Result.GetResource<T>();
            }
            catch (AggregateException aggregatedException)
            {
                throw aggregatedException.InnerException;
            }
        }


        public static T Update<T>(this DocumentClient client, T resource, INameValueCollection requestHeaders = null) where T : CosmosResource, new()
        {
            try
            {
                String link = resource.GetLink();
                DocumentServiceRequest request;

                if (link != null)
                {
                    request = DocumentServiceRequest.Create(
                        OperationType.Replace,
                        link,
                        resource,
                        TestCommon.ToResourceType(typeof(T)),
                        AuthorizationTokenType.PrimaryMasterKey,
                        requestHeaders);
                }
                else
                {
                    request = DocumentServiceRequest.Create(
                        OperationType.Replace,
                        resource,
                        TestCommon.ToResourceType(typeof(T)),
                        AuthorizationTokenType.PrimaryMasterKey,
                        requestHeaders);
                }

                if (request.ResourceType.IsPartitioned())
                {
                    request.Headers[HttpConstants.HttpHeaders.PartitionKey] = PartitionKeyInternal.Empty.ToJsonString();
                }

                DocumentServiceResponse response = client.UpdateAsync(request).Result;
                return response.GetResource<T>();
            }
            catch (AggregateException aggregatedException)
            {
                throw aggregatedException.InnerException;
            }
        }

        public static T Replace<T>(this DocumentClient client, T resource) where T : CosmosResource, new()
        {
            return client.Update(resource);
        }

        public static DocumentServiceResponse Delete<T>(this DocumentClient client, string resourceIdOrFullName, INameValueCollection headers = null) where T : CosmosResource, new()
        {
            try
            {
                DocumentServiceRequest request;
                if (PathsHelper.IsNameBased(resourceIdOrFullName))
                {
                    request = DocumentServiceRequest.CreateFromName(
                        OperationType.Delete,
                        resourceIdOrFullName,
                        TestCommon.ToResourceType(typeof(T)),
                        AuthorizationTokenType.PrimaryMasterKey,
                        headers);
                }
                else
                {
                    request = DocumentServiceRequest.Create(
                        OperationType.Delete,
                        resourceIdOrFullName,
                        TestCommon.ToResourceType(typeof(T)),
                        AuthorizationTokenType.PrimaryMasterKey,
                        headers);
                }

                if (request.ResourceType.IsPartitioned())
                {
                    request.Headers[HttpConstants.HttpHeaders.PartitionKey] = PartitionKeyInternal.Empty.ToJsonString();
                }

                return client.DeleteAsync(request).Result;
            }
            catch (AggregateException aggregatedException)
            {
                if (aggregatedException.InnerException is NotFoundException)
                {
                    return new DocumentServiceResponse(null, null, HttpStatusCode.NotFound);
                }
                else
                {
                    throw aggregatedException.InnerException;
                }
            }
        }

        public static FeedResponse<T> ReadFeed<T>(
            this DocumentClient client,
            string resourceIdOrFullName,
            INameValueCollection headers = null) where T : CosmosResource, new()
        {
            INameValueCollection ignored;
            return client.ReadFeed<T>(resourceIdOrFullName, out ignored, headers);
        }

        public static FeedResponse<T> ReadFeed<T>(
            this DocumentClient client,
            string resourceIdOrFullName,
            out INameValueCollection responseHeaders,
            INameValueCollection headers = null) where T : CosmosResource, new()
        {
            try
            {
                DocumentServiceRequest request;
                if (PathsHelper.IsNameBased(resourceIdOrFullName))
                {
                    request = DocumentServiceRequest.CreateFromName(
                        OperationType.ReadFeed,
                        resourceIdOrFullName,
                        TestCommon.ToResourceType(typeof(T)),
                        AuthorizationTokenType.PrimaryMasterKey,
                        headers);
                }
                else
                {
                    request = DocumentServiceRequest.Create(
                        OperationType.ReadFeed,
                        resourceIdOrFullName,
                        TestCommon.ToResourceType(typeof(T)),
                        AuthorizationTokenType.PrimaryMasterKey,
                        headers);
                }

                if (request.ResourceType.IsPartitioned())
                {
                    ClientCollectionCache collectionCache = client.GetCollectionCacheAsync().Result;
                    CosmosContainerSettings collection = collectionCache.ResolveCollectionAsync(request, CancellationToken.None).Result;
                    IRoutingMapProvider routingMapProvider = client.GetPartitionKeyRangeCacheAsync().Result;
                    IReadOnlyList<PartitionKeyRange> overlappingRanges = routingMapProvider.TryGetOverlappingRangesAsync(
                        collection.ResourceId,
                        Range<string>.GetPointRange(PartitionKeyInternal.MinimumInclusiveEffectivePartitionKey)).Result;

                    request.RouteTo(new PartitionKeyRangeIdentity(collection.ResourceId, overlappingRanges.Single().Id));
                }

                DocumentServiceResponse result = client.ReadFeedAsync(request).Result;
                responseHeaders = result.Headers;
                FeedResource<T> feedResource = result.GetResource<FeedResource<T>>();

                return new FeedResponse<T>(feedResource,
                    feedResource.Count,
                    result.Headers);
            }
            catch (AggregateException aggregatedException)
            {
                throw aggregatedException.InnerException;
            }
        }

        public static void AssertException(DocumentClientException clientException, params HttpStatusCode[] statusCodes)
        {
            Assert.IsNotNull(clientException.Error, "Exception.Error is null");
            Assert.IsNotNull(clientException.ActivityId, "Exception.ActivityId is null");

            if (statusCodes.Length == 1)
            {
                Assert.AreEqual(clientException.Error.Code, statusCodes[0].ToString(), string.Format(CultureInfo.InvariantCulture, "Error code dont match, details {0}", clientException.ToString()));
            }
            else
            {
                bool matched = false;

                foreach (HttpStatusCode statusCode in statusCodes)
                {
                    if (statusCode.ToString() == clientException.Error.Code)
                    {
                        matched = true;
                        break;
                    }
                }

                if (!matched)
                {
                    Assert.Fail("Exception code {0}, didnt match any of the expected exception codes {1}, details {2}",
                        clientException.Error.Code,
                        string.Join(",", statusCodes),
                        clientException.Message);
                }
            }
        }

        public static async Task<string> CreateRandomBinaryFileInTmpLocation(long fileSizeInBytes)
        {
            string filePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString() + ".bin");
            await TestCommon.CreateFileWithRandomBytesAsync(filePath, fileSizeInBytes);
            return filePath;
        }

        public static async Task CreateFileWithRandomBytesAsync(string filePath,
            long fileSizeInBytes)
        {
            Logger.LogLine("Creating file at {0} with fileSizeInBytes {1}", filePath, fileSizeInBytes);

            long remaingBytesToWrite = fileSizeInBytes;
            long bufferSize = 1048576;

            Random random = new Random();

            using (FileStream fileStream = File.Open(filePath, FileMode.CreateNew, FileAccess.Write))
            {
                while (remaingBytesToWrite > 0)
                {
                    Byte[] buffer = new Byte[Math.Min(bufferSize, remaingBytesToWrite)];
                    random.NextBytes(buffer);


                    await fileStream.WriteAsync(buffer, 0, buffer.Length);

                    remaingBytesToWrite -= bufferSize;
                }
            }

        }

#region Environment Configuration Helpers
        private static ReplicationPolicy GetServerReplicationPolicy()
        {
            return new ReplicationPolicy
            {
                MaxReplicaSetSize = 3,
                MinReplicaSetSize = 2,
                AsyncReplication = true
            };
        }

        private static CosmosConsistencySettings GetServerConsistencyPolicy()
        {
            CosmosConsistencySettings consistencyPolicy = new CosmosConsistencySettings
            {
                DefaultConsistencyLevel = ConsistencyLevel.Strong
            };

            return consistencyPolicy;
        }

#endregion

        public static string GetCollectionOfferDetails(DocumentClient client, string collectionResourceId)
        {
            using (ActivityScope scope = new ActivityScope(Guid.NewGuid()))
            {
                var offer = client.CreateOfferQuery().Where(o => o.OfferResourceId == collectionResourceId).AsEnumerable().FirstOrDefault(); ;
                OfferV2 offerV2 = null;
                try
                {
                    offerV2 = (OfferV2)offer;
                }
                catch
                {
                    ;
                }

                if(offerV2 != null)
                {
                    return offerV2.OfferType;
                }

                if (offer != null)
                {
                    return offer.OfferType;
                }

                return null;
            }
        }

        public static void SetIntConfigurationProperty(string propertyName, int value)
        {
            // There is no federation configuration in the Public Emulator
        }

        public static async Task SetIntConfigurationPropertyAsync(string propertyName, int value)
        {
            // There is no federation configuration in the Public Emulator

            await Task.FromResult<bool>(default(bool));
        }

        public static void SetStringConfigurationProperty(string propertyName, string value)
        {
            // There is no federation configuration in the Public Emulator
        }

        public static void SetBooleanConfigurationProperty(string propertyName, bool value)
        {
            // There is no federation configuration in the Public Emulator
        }

        public static void SetDoubleConfigurationProperty(string propertyName, double value)
        {
            // There is no federation configuration in the Public Emulator
        }

        public static void SetFederationWideConfigurationProperty<T>(string propertyName, T value)
        {
            // There is no federation configuration in the Public Emulator
        }

        public static void WaitForConfigRefresh()
        {
            // There is no federation configuration in the Public Emulator
        }

        public static Task WaitForConfigRefreshAsync()
        {
            // There is no federation configuration in the Public Emulator
            return Task.Delay(0);
        }

        public static void WaitForBackendConfigRefresh()
        {
            // There is no federation configuration in the Public Emulator
        }

        public static async Task DeleteAllDatabasesAsync(DocumentClient client)
        {
            int numberOfRetry = 3;
            DocumentClientException finalException = null;
            do
            {
                TimeSpan retryAfter = TimeSpan.Zero;
                try
                {
                    await TestCommon.DeleteAllDatabasesAsyncWorker(client);

                    FeedResponse<Offer> offerResponse = client.ReadOffersFeedAsync().Result;
                    if (offerResponse.Count != 0)
                    {
                        // Number of offers should have been 0 after deleting all the databases
                        string error = string.Format("All offers not deleted after DeleteAllDatabases. Number of offer remaining {0}",
                            offerResponse.Count);
                        Logger.LogLine(error);
                        Logger.LogLine("Remaining offers are: ");
                        foreach (Offer offer in offerResponse)
                        {
                            Logger.LogLine("Offer resourceId: {0}, offer resourceLink: {1}", offer.OfferResourceId, offer.ResourceLink);
                        }

                        //Assert.Fail(error);
                    }

                    return;
                }
                catch (DocumentClientException clientException)
                {
                    finalException = clientException;
                    if (clientException.StatusCode == (HttpStatusCode)429)
                    {
                        Logger.LogLine("Received request rate too large. ActivityId: {0}, {1}",
                                       clientException.ActivityId,
                                       clientException);

                        retryAfter = TimeSpan.FromSeconds(1);
                    }
                    else if (clientException.StatusCode == HttpStatusCode.RequestTimeout)
                    {
                        Logger.LogLine("Received timeout exception while cleaning the store. ActivityId: {0}, {1}",
                                       clientException.ActivityId,
                                       clientException);

                        retryAfter = TimeSpan.FromSeconds(1);
                    }
                    else if (clientException.StatusCode == HttpStatusCode.NotFound)
                    {
                        // Previous request (that timed-out) might have been committed to the store.
                        // In such cases, ignore the not-found exception.
                        Logger.LogLine("Received not-found exception while cleaning the store. ActivityId: {0}, {1}",
                                       clientException.ActivityId,
                                       clientException);
                    }
                    else
                    {
                        Logger.LogLine("Unexpected exception. ActivityId: {0}, {1}", clientException.ActivityId, clientException);
                    }
                    if (numberOfRetry == 1) throw;
                }

                if (retryAfter > TimeSpan.Zero)
                {
                    await Task.Delay(retryAfter);
                }

            } while (numberOfRetry-- > 0);
        }

        public static async Task DeleteAllDatabasesAsyncWorker(DocumentClient client)
        {
            do
            {
                IList<CosmosDatabaseSettings> databases = TestCommon.RetryRateLimiting(
                    () => TestCommon.ListAll<CosmosDatabaseSettings>(client, null));

                Logger.LogLine("Number of database to delete {0}", databases.Count);

                if (databases.Count == 0) return;

                List<Task> deleteTasks = new List<Task>(10); //Delete in chunks of 10

                foreach (CosmosDatabaseSettings database in databases)
                {
                    if (deleteTasks.Count == 10) break;
                    deleteTasks.Add(TestCommon.DeleteDatabaseAsync(client, database));
                }

                await Task.WhenAll(deleteTasks);
            } while (true);
        }

        public static async Task DeleteDatabaseAsync(DocumentClient client, CosmosDatabaseSettings database)
        {
            await TestCommon.DeleteDatabaseCollectionAsync(client, database);

            await TestCommon.AsyncRetryRateLimiting(() => client.DeleteDatabaseAsync(database.SelfLink));
        }

        public static async Task DeleteDatabaseCollectionAsync(DocumentClient client, CosmosDatabaseSettings database)
        {
            do //Delete them in chunks of 10.
            {
                IList<CosmosContainerSettings> collections = TestCommon.RetryRateLimiting(
                    () => TestCommon.ListAll<CosmosContainerSettings>(client, database.ResourceId));

                Logger.LogLine("Number of Collections {1}  to delete in database {0}", database.ResourceId, collections.Count);

                if (collections.Count != 0)
                {
                    List<Task> deleteCollectionTasks = new List<Task>(10);

                    foreach (CosmosContainerSettings collection in collections)
                    {
                        if (deleteCollectionTasks.Count == 10) break;
                        Logger.LogLine("Deleting Collection with following info Id:{0},SelfLink:{1}", collection.ResourceId, collection.SelfLink);

                        deleteCollectionTasks.Add(TestCommon.AsyncRetryRateLimiting(() => client.DeleteDocumentCollectionAsync(collection.SelfLink)));
                    }
                    await Task.WhenAll(deleteCollectionTasks);
                }
                else
                {
                    break;
                }
            } while (true);


        }

        public static async Task<T> AsyncRetryRateLimiting<T>(Func<Task<T>> work)
        {
            while (true)
            {
                TimeSpan retryAfter = TimeSpan.FromSeconds(1);

                try
                {
                    return await work();
                }
                catch (DocumentClientException e)
                {
                    if ((int)e.StatusCode == (int) StatusCodes.TooManyRequests)
                    {
                        if (e.RetryAfter.TotalMilliseconds > 0)
                        {
                            retryAfter = new[] { e.RetryAfter, TimeSpan.FromSeconds(1) }.Max();
                        }
                    }
                    else
                    {
                        throw;
                    }
                }

                await Task.Delay(retryAfter);
            }
        }

        public static T RetryRateLimiting<T>(Func<T> work)
        {
            while (true)
            {
                TimeSpan retryAfter = TimeSpan.FromSeconds(1);

                try
                {
                    return work();
                }
                catch (Exception e)
                {
                    while (e is AggregateException)
                    {
                        e = e.InnerException;
                    }

                    DocumentClientException clientException = e as DocumentClientException;
                    if (clientException == null)
                    {
                        throw;
                    }

                    if ((int)clientException.StatusCode == (int)StatusCodes.TooManyRequests)
                    {
                        retryAfter = new[] { clientException.RetryAfter, TimeSpan.FromSeconds(1) }.Max();
                    }
                    else
                    {
                        throw;
                    }
                }

                Task.Delay(retryAfter);
            }
        }

        /// <summary>
        /// Timed wait while the backend operation commit happens.
        /// </summary>
        public static void WaitWhileBackendOperationCommits()
        {
            Task.Delay(TimeSpan.FromSeconds(TestCommon.TimeToWaitForOperationCommitInSec)).Wait();
        }

        internal static ResourceType ToResourceType(Type type)
        {
            if (type == typeof(Conflict))
            {
                return ResourceType.Conflict;
            }
            else if (type == typeof(CosmosDatabaseSettings))
            {
                return ResourceType.Database;
            }
            else if (type == typeof(CosmosContainerSettings))
            {
                return ResourceType.Collection;
            }
            else if (type == typeof(Document) || typeof(Document).IsAssignableFrom(type))
            {
                return ResourceType.Document;
            }
            else if (type == typeof(Permission))
            {
                return ResourceType.Permission;
            }
            else if (type == typeof(CosmosStoredProcedureSettings))
            {
                return ResourceType.StoredProcedure;
            }
            else if (type == typeof(CosmosTriggerSettings))
            {
                return ResourceType.Trigger;
            }
            else if (type == typeof(CosmosUserDefinedFunctionSettings))
            {
                return ResourceType.UserDefinedFunction;
            }
            else if (type == typeof(User))
            {
                return ResourceType.User;
            }
            else if (type == typeof(Attachment))
            {
                return ResourceType.Attachment;
            }
            else if (type == typeof(Offer) || type == typeof(Offer))
            {
                return ResourceType.Offer;
            }
            else if (type == typeof(Schema))
            {
                return ResourceType.Schema;
            }
            else if (type == typeof(PartitionKeyRange))
            {
                return ResourceType.PartitionKeyRange;
            }
            else
            {
                string errorMessage = string.Format(CultureInfo.CurrentUICulture, RMResources.UnknownResourceType, type.Name);
                throw new ArgumentException(errorMessage);
            }
        }

        public static async Task<CosmosContainerSettings> CreateCollectionAsync(
            DocumentClient client,
            Uri dbUri,
            CosmosContainerSettings col,
            RequestOptions requestOptions = null)
        {
            return await client.CreateDocumentCollectionAsync(
                dbUri,
                col,
                requestOptions);
        }

        public static async Task<CosmosContainerSettings> CreateCollectionAsync(
            DocumentClient client,
            string databaseSelfLink,
            CosmosContainerSettings col,
            RequestOptions requestOptions = null)
        {
            return await client.CreateDocumentCollectionAsync(
                databaseSelfLink,
                col,
                requestOptions);
        }

        public static async Task<CosmosContainerSettings> CreateCollectionAsync(
            DocumentClient client,
            CosmosDatabaseSettings db,
            CosmosContainerSettings col,
            RequestOptions requestOptions = null)
        {
            return await client.CreateDocumentCollectionAsync(
                db,
                col,
                requestOptions);
        }

        public static async Task<ResourceResponse<CosmosContainerSettings>> CreateDocumentCollectionResourceAsync(
            DocumentClient client,
            CosmosDatabaseSettings db,
            CosmosContainerSettings col,
            RequestOptions requestOptions = null)
        {
            return await client.CreateDocumentCollectionAsync(
                db,
                col,
                requestOptions);
        }

        public static async Task<ResourceResponse<CosmosContainerSettings>> CreateDocumentCollectionResourceAsync(
            DocumentClient client,
            string dbSelflink,
            CosmosContainerSettings col,
            RequestOptions requestOptions = null)
        {
            return await client.CreateDocumentCollectionAsync(
                dbSelflink,
                col,
                requestOptions);
        }

        public static ISessionToken CreateSessionToken(ISessionToken from, long globalLSN)
        {
            // Creates session token with specified GlobalLSN
            if (from is SimpleSessionToken)
            {
                return new SimpleSessionToken(globalLSN);
            }
            else if (from is VectorSessionToken)
            {
                return new VectorSessionToken(from as VectorSessionToken, globalLSN);
            }
            else
            {
                throw new ArgumentException();
            }
        }

        private class DisposableList : IDisposable
        {
            private List<IDisposable> disposableList;

            internal DisposableList()
            {
                this.disposableList = new List<IDisposable>();
            }

            internal void Add(IDisposable disposable)
            {
                this.disposableList.Add(disposable);
            }

            public void Dispose()
            {
                foreach (IDisposable disposable in this.disposableList)
                {
                    disposable.Dispose();
                }

                TestCommon.WaitForConfigRefresh();
            }
        }

        private class RestoreNamingConfigurations : IDisposable
        {
            private Uri parentName;
            private List<KeyValuePair<string, string>> keyValues;

            internal RestoreNamingConfigurations(Uri parentName, List<KeyValuePair<string, string>> keyValues)
            {
                this.parentName = parentName;
                this.keyValues = keyValues;
            }

            public void Dispose()
            {
                TestCommon.WaitForConfigRefresh();
            }
        }
    }
}