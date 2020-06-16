﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Fluent;
using Microsoft.Extensions.Logging;
using Microsoft.Health.CosmosDb.Configs;
using Microsoft.Health.CosmosDb.Features.Queries;
using Microsoft.Health.CosmosDb.Features.Storage;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.CosmosDb.Features.Storage.Search;
using Microsoft.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using JsonSerializer = Newtonsoft.Json.JsonSerializer;

namespace Microsoft.Health.Fhir.CosmosDb.Features.Storage
{
    public class FhirDocumentClientInitializer : IDocumentClientInitializer
    {
        private readonly IDocumentClientTestProvider _testProvider;
        private readonly IFhirRequestContextAccessor _fhirRequestContextAccessor;
        private readonly ILogger<FhirDocumentClientInitializer> _logger;
        private readonly ICosmosResponseProcessor _cosmosResponseProcessor;

        public FhirDocumentClientInitializer(IDocumentClientTestProvider testProvider, IFhirRequestContextAccessor fhirRequestContextAccessor, ICosmosResponseProcessor cosmosResponseProcessor, ILogger<FhirDocumentClientInitializer> logger)
        {
            EnsureArg.IsNotNull(testProvider, nameof(testProvider));
            EnsureArg.IsNotNull(fhirRequestContextAccessor, nameof(fhirRequestContextAccessor));
            EnsureArg.IsNotNull(cosmosResponseProcessor, nameof(cosmosResponseProcessor));
            EnsureArg.IsNotNull(logger, nameof(logger));

            _testProvider = testProvider;
            _fhirRequestContextAccessor = fhirRequestContextAccessor;
            _cosmosResponseProcessor = cosmosResponseProcessor;
            _logger = logger;
        }

        /// <inheritdoc />
        public CosmosClient CreateDocumentClient(CosmosDataStoreConfiguration configuration)
        {
            EnsureArg.IsNotNull(configuration, nameof(configuration));

            _logger.LogInformation("Creating DocumentClient instance for {DatabaseId}", configuration.DatabaseId);

            var host = configuration.Host;
            var key = configuration.Key;

            if (string.IsNullOrWhiteSpace(host) && string.IsNullOrWhiteSpace(key))
            {
                host = CosmosDbLocalEmulator.Host;
                key = CosmosDbLocalEmulator.Key;
            }

            var builder = new CosmosClientBuilder(host, key)
                .WithConnectionModeDirect()
                .WithCustomSerializer(new NewtonsoftSerializer())
                .WithThrottlingRetryOptions(TimeSpan.FromSeconds(configuration.RetryOptions.MaxWaitTimeInSeconds), configuration.RetryOptions.MaxNumberOfRetries);

            if (configuration.PreferredLocations?.Any() == true)
            {
                builder.WithApplicationPreferredRegions(configuration.PreferredLocations?.ToArray());
            }

            if (configuration.DefaultConsistencyLevel != null)
            {
                builder.WithConsistencyLevel(configuration.DefaultConsistencyLevel.Value);
            }

            return builder.Build();
        }

        public Container CreateFhirContainer(CosmosClient client, string databaseId, string collectionId, int? continuationTokenSizeLimitInKb)
        {
            return new FhirContainer(
                client,
                client.GetContainer(databaseId, collectionId),
                _fhirRequestContextAccessor,
                continuationTokenSizeLimitInKb,
                _cosmosResponseProcessor);
        }

        /// <inheritdoc />
        public async Task OpenDocumentClient(CosmosClient client, CosmosDataStoreConfiguration configuration, CosmosCollectionConfiguration cosmosCollectionConfiguration)
        {
            EnsureArg.IsNotNull(client, nameof(client));
            EnsureArg.IsNotNull(configuration, nameof(configuration));

            _logger.LogInformation("Opening DocumentClient connection to {CollectionId}", cosmosCollectionConfiguration.CollectionId);
            try
            {
                await _testProvider.PerformTest(client.GetContainer(configuration.DatabaseId, cosmosCollectionConfiguration.CollectionId), configuration, cosmosCollectionConfiguration);

                _logger.LogInformation("Established DocumentClient connection to {CollectionId}", cosmosCollectionConfiguration.CollectionId);
            }
            catch (Exception e)
            {
                _logger.LogCritical(e, "Failed to connect to DocumentClient collection {CollectionId}", cosmosCollectionConfiguration.CollectionId);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task InitializeDataStore(CosmosClient documentClient, CosmosDataStoreConfiguration cosmosDataStoreConfiguration, IEnumerable<ICollectionInitializer> collectionInitializers)
        {
            EnsureArg.IsNotNull(documentClient, nameof(documentClient));
            EnsureArg.IsNotNull(cosmosDataStoreConfiguration, nameof(cosmosDataStoreConfiguration));
            EnsureArg.IsNotNull(collectionInitializers, nameof(collectionInitializers));

            try
            {
                _logger.LogInformation("Initializing Cosmos DB Database {DatabaseId} and collections", cosmosDataStoreConfiguration.DatabaseId);

                if (cosmosDataStoreConfiguration.AllowDatabaseCreation)
                {
                    _logger.LogDebug("CreateDatabaseIfNotExists {DatabaseId})", cosmosDataStoreConfiguration.DatabaseId);

                    await documentClient.CreateDatabaseIfNotExistsAsync(
                        cosmosDataStoreConfiguration.DatabaseId,
                        cosmosDataStoreConfiguration.InitialDatabaseThroughput.HasValue ? ThroughputProperties.CreateManualThroughput(cosmosDataStoreConfiguration.InitialDatabaseThroughput.Value) : null);
                }

                foreach (var collectionInitializer in collectionInitializers)
                {
                    await collectionInitializer.InitializeCollection(documentClient);
                }

                _logger.LogInformation("Cosmos DB Database {DatabaseId} and collections successfully initialized", cosmosDataStoreConfiguration.DatabaseId);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Cosmos DB Database {DatabaseId} and collections initialization failed", cosmosDataStoreConfiguration.DatabaseId);
                throw;
            }
        }

        private class NewtonsoftSerializer : CosmosSerializer
        {
            private readonly JsonSerializer _serializer;
            private RecyclableMemoryStreamManager _manager = new RecyclableMemoryStreamManager();
            private JsonSerializerOptions options;
            ////private static readonly Type _insertModelType = typeof(UpsertWithHistoryModel);
            ////private static readonly Type _wrapperType = typeof(ResourceWrapper);

            public NewtonsoftSerializer()
            {
                // Setting TypeNameHandling to any value other than 'None' will be flagged
                // as causing potential security issues
                var serializerSettings = new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore,
                    DateFormatHandling = DateFormatHandling.IsoDateFormat,
                    DateParseHandling = DateParseHandling.DateTimeOffset,
                    DateTimeZoneHandling = DateTimeZoneHandling.RoundtripKind,
                    TypeNameHandling = TypeNameHandling.None,
                };

                serializerSettings.Converters.Add(new StringEnumConverter());

                // By default, the Json.NET serializer uses 'F' instead of 'f' for fractions.
                // 'F' will omit the trailing digits if they are 0. You might end up getting something like '2018-02-07T20:04:49.97114+00:00'
                // where the fraction is actually 971140. Because the ordering is done as string,
                // if the values don't always have complete 7 digits, the comparison might not work properly.
                serializerSettings.Converters.Add(new IsoDateTimeConverter { DateTimeFormat = "o" });

                _serializer = JsonSerializer.Create(serializerSettings);

                options = new System.Text.Json.JsonSerializerOptions
                {
                    IgnoreNullValues = true,
                };
                options.Converters.Add(new JsonStringEnumConverter());
                options.Converters.Add(new SearchIndexEntryJsonConverter());
            }

            public override T FromStream<T>(Stream stream)
            {
                ////Type type = typeof(T);
                ////if (type.IsAssignableFrom(_wrapperType) ||
                ////    type.IsAssignableFrom(_insertModelType))
                ////{
                ////    try
                ////    {
                ////        return System.Text.Json.JsonSerializer.DeserializeAsync<T>(stream, options).GetAwaiter().GetResult();
                ////    }
                ////    finally
                ////    {
                ////        stream.Dispose();
                ////    }
                ////}

                using var textReader = new StreamReader(stream);
                using var reader = new JsonTextReader(textReader);
                return _serializer.Deserialize<T>(reader);
            }

            public override Stream ToStream<T>(T input)
            {
                ////if (input is ResourceWrapper || (input is object[] objArray && objArray.Any(x => x is ResourceWrapper)))
                ////{
                ////    MemoryStream stream = _manager.GetStream();
                ////    var utf8JsonWriter = new Utf8JsonWriter(stream);
                ////    System.Text.Json.JsonSerializer.Serialize(utf8JsonWriter, input, options);
                ////    return stream;
                ////}
                ////else
                ////{
                MemoryStream stream = _manager.GetStream();
                var writer = new StreamWriter(stream);
                var jsonWriter = new JsonTextWriter(writer);
                _serializer.Serialize(jsonWriter, input);
                jsonWriter.Flush();
                writer.Flush();
                stream.Seek(0, 0);
                return stream;
                ////}
            }
        }
    }
}
