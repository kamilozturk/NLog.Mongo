﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using MongoDB.Bson;
using MongoDB.Driver;
using NLog.Common;
using NLog.Config;
using NLog.Targets;
using System.Collections;

namespace NLog.Mongo
{
    /// <summary>
    /// NLog message target for MongoDB.
    /// </summary>
    [Target("Mongo")]
    public class MongoTarget : Target
    {
        private static readonly ConcurrentDictionary<string, IMongoCollection<BsonDocument>> _collectionCache = new ConcurrentDictionary<string, IMongoCollection<BsonDocument>>();

        /// <summary>
        /// Initializes a new instance of the <see cref="MongoTarget"/> class.
        /// </summary>
        public MongoTarget()
        {
            Fields = new List<MongoField>();
            Properties = new List<MongoField>();
            IncludeDefaults = true;
        }

        /// <summary>
        /// Gets the fields collection.
        /// </summary>
        /// <value>
        /// The fields.
        /// </value>
        [ArrayParameter(typeof(MongoField), "field")]
        public IList<MongoField> Fields { get; private set; }

        /// <summary>
        /// Gets the properties collection.
        /// </summary>
        /// <value>
        /// The properties.
        /// </value>
        [ArrayParameter(typeof(MongoField), "property")]
        public IList<MongoField> Properties { get; private set; }

        /// <summary>
        /// Gets or sets the connection string name string.
        /// </summary>
        /// <value>
        /// The connection name string.
        /// </value>
        public string ConnectionString { get; set; }

        /// <summary>
        /// Gets or sets the name of the connection.
        /// </summary>
        /// <value>
        /// The name of the connection.
        /// </value>
        public string ConnectionName { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to use the default document format.
        /// </summary>
        /// <value>
        ///   <c>true</c> to use the default document format; otherwise, <c>false</c>.
        /// </value>
        public bool IncludeDefaults { get; set; }

        /// <summary>
        /// Gets or sets the name of the database.
        /// </summary>
        /// <value>
        /// The name of the database.
        /// </value>
        public string DatabaseName { get; set; }

        /// <summary>
        /// Gets or sets the name of the collection.
        /// </summary>
        /// <value>
        /// The name of the collection.
        /// </value>
        public string CollectionName { get; set; }

        /// <summary>
        /// Gets or sets the size in bytes of the capped collection.
        /// </summary>
        /// <value>
        /// The size of the capped collection.
        /// </value>
        public long? CappedCollectionSize { get; set; }

        /// <summary>
        /// Gets or sets the capped collection max items.
        /// </summary>
        /// <value>
        /// The capped collection max items.
        /// </value>
        public long? CappedCollectionMaxItems { get; set; }

        /// <summary>
        /// Initializes the target. Can be used by inheriting classes
        /// to initialize logging.
        /// </summary>
        /// <exception cref="NLog.NLogConfigurationException">Can not resolve MongoDB ConnectionString. Please make sure the ConnectionString property is set.</exception>
        protected override void InitializeTarget()
        {
            base.InitializeTarget();

            if (!string.IsNullOrEmpty(ConnectionName))
                ConnectionString = GetConnectionString(ConnectionName);

            if (string.IsNullOrEmpty(ConnectionString))
                throw new NLogConfigurationException("Can not resolve MongoDB ConnectionString. Please make sure the ConnectionString property is set.");

        }

        /// <summary>
        /// Writes an array of logging events to the log target. By default it iterates on all
        /// events and passes them to "Write" method. Inheriting classes can use this method to
        /// optimize batch writes.
        /// </summary>
        /// <param name="logEvents">Logging events to be written out.</param>
        protected override void Write(AsyncLogEventInfo[] logEvents)
        {
            if (logEvents.Length == 0)
                return;

            try
            {
                var documents = logEvents.Select(e => CreateDocument(e.LogEvent));

                var collection = GetCollection();
                collection.InsertMany(documents);

                foreach (var ev in logEvents)
                    ev.Continuation(null);

            }
            catch (Exception ex)
            {
                if (ex is StackOverflowException || ex is ThreadAbortException || ex is OutOfMemoryException || ex is NLogConfigurationException)
                    throw;

                InternalLogger.Error("Error when writing to MongoDB {0}", ex);

                foreach (var ev in logEvents)
                    ev.Continuation(ex);

            }
        }

        /// <summary>
        /// Writes logging event to the log target.
        /// classes.
        /// </summary>
        /// <param name="logEvent">Logging event to be written out.</param>
        protected override void Write(LogEventInfo logEvent)
        {
            try
            {
                var document = CreateDocument(logEvent);
                var collection = GetCollection();
                collection.InsertOne(document);
            }
            catch (Exception ex)
            {
                if (ex is StackOverflowException || ex is ThreadAbortException || ex is OutOfMemoryException || ex is NLogConfigurationException)
                    throw;

                InternalLogger.Error("Error when writing to MongoDB {0}", ex);
            }
        }


        private BsonDocument CreateDocument(LogEventInfo logEvent)
        {
            var document = new BsonDocument();
            if (IncludeDefaults || Fields.Count == 0)
                AddDefaults(document, logEvent);

            // extra fields
            foreach (var field in Fields)
            {
                var value = GetValue(field, logEvent);
                if (value != null)
                    document[field.Name] = value;
            }

            AddProperties(document, logEvent);

            return document;
        }

        private void AddDefaults(BsonDocument document, LogEventInfo logEvent)
        {
            document.Add("Date", new BsonDateTime(logEvent.TimeStamp));

            if (logEvent.Level != null)
                document.Add("Level", new BsonString(logEvent.Level.Name));

            if (logEvent.LoggerName != null)
                document.Add("Logger", new BsonString(logEvent.LoggerName));

            if (logEvent.FormattedMessage != null)
                document.Add("Message", new BsonString(logEvent.FormattedMessage));

            if (logEvent.Exception != null)
                document.Add("Exception", CreateException(logEvent.Exception));


        }

        private void AddProperties(BsonDocument document, LogEventInfo logEvent)
        {
            var propertiesDocument = new BsonDocument();

            foreach (var field in Properties)
            {
                string key = field.Name;
                var value = GetValue(field, logEvent);

                if (value != null)
                    propertiesDocument[key] = value;
            }

            var properties = logEvent.Properties ?? Enumerable.Empty<KeyValuePair<object, object>>();

            foreach (var property in properties)
            {
                if (property.Key == null || property.Value == null)
                    continue;

                string key = Convert.ToString(property.Key, CultureInfo.InvariantCulture);

                if (property.Value is Int32)
                {
                    propertiesDocument[key] = new BsonInt32((Int32)property.Value);
                }
                else if (property.Value is Int64)
                {
                    propertiesDocument[key] = new BsonInt64((Int64)property.Value);
                }
                else if (property.Value is bool)
                {
                    propertiesDocument[key] = new BsonBoolean((bool)property.Value);
                }
                else if (property.Value is DateTime)
                {
                    propertiesDocument[key] = new BsonDateTime((DateTime)property.Value);
                }
                else if (property.Value is decimal)
                {
                    propertiesDocument[key] = new BsonDecimal128((decimal)property.Value);
                }
                else if (property.Value is double)
                {
                    propertiesDocument[key] = new BsonDouble((double)property.Value);
                }
                else if (property.Value.GetType().IsArray)
                {
                    propertiesDocument[key] = new BsonArray(property.Value as IEnumerable);
                }
                else if (property.Value is IDictionary)
                {
                    propertiesDocument[key] = new BsonDocument(property.Value as IDictionary);
                }
                else if (property.Value is string)
                {
                    string value = Convert.ToString(property.Value, CultureInfo.InvariantCulture);

                    if (!string.IsNullOrEmpty(value))
                        propertiesDocument[key] = new BsonString(value);
                }
                else
                {
                    propertiesDocument[key] = property.Value.ToBsonDocument();
                }
            }

            if (propertiesDocument.ElementCount > 0)
                document.Merge(propertiesDocument);
        }

        private BsonValue CreateException(Exception exception)
        {
            if (exception == null)
                return BsonNull.Value;

            var document = new BsonDocument();
            document.Add("Message", new BsonString(exception.Message));
            document.Add("BaseMessage", new BsonString(exception.GetBaseException().Message));
            document.Add("Text", new BsonString(exception.ToString()));
            document.Add("Type", new BsonString(exception.GetType().ToString()));

            var external = exception as ExternalException;
            if (external != null)
                document.Add("ErrorCode", new BsonInt32(external.ErrorCode));

            document.Add("Source", new BsonString(exception.Source));

            MethodBase method = exception.TargetSite;
            if (method != null)
            {
                document.Add("MethodName", new BsonString(method.Name));

                AssemblyName assembly = method.Module.Assembly.GetName();
                document.Add("ModuleName", new BsonString(assembly.Name));
                document.Add("ModuleVersion", new BsonString(assembly.Version.ToString()));
            }

            return document;
        }


        private BsonValue GetValue(MongoField field, LogEventInfo logEvent)
        {
            var value = field.Layout.Render(logEvent);
            if (string.IsNullOrWhiteSpace(value))
                return null;

            value = value.Trim();

            if (string.IsNullOrEmpty(field.BsonType)
                || string.Equals(field.BsonType, "String", StringComparison.OrdinalIgnoreCase))
                return new BsonString(value);


            BsonValue bsonValue;
            if (string.Equals(field.BsonType, "Boolean", StringComparison.OrdinalIgnoreCase)
                && MongoConvert.TryBoolean(value, out bsonValue))
                return bsonValue;

            if (string.Equals(field.BsonType, "DateTime", StringComparison.OrdinalIgnoreCase)
                && MongoConvert.TryDateTime(value, out bsonValue))
                return bsonValue;

            if (string.Equals(field.BsonType, "Double", StringComparison.OrdinalIgnoreCase)
                && MongoConvert.TryDouble(value, out bsonValue))
                return bsonValue;

            if (string.Equals(field.BsonType, "Int32", StringComparison.OrdinalIgnoreCase)
                && MongoConvert.TryInt32(value, out bsonValue))
                return bsonValue;

            if (string.Equals(field.BsonType, "Int64", StringComparison.OrdinalIgnoreCase)
                && MongoConvert.TryInt64(value, out bsonValue))
                return bsonValue;

            return new BsonString(value);
        }

        private IMongoCollection<BsonDocument> GetCollection()
        {
            // cache mongo collection based on target name.
            string key = string.Format("k|{0}|{1}|{2}",
                ConnectionName ?? string.Empty,
                ConnectionString ?? string.Empty,
                CollectionName ?? string.Empty);

            return _collectionCache.GetOrAdd(key, k =>
            {
                // create collection
                var mongoUrl = new MongoUrl(ConnectionString);
                var client = new MongoClient(mongoUrl);

                // Database name overrides connection string
                var databaseName = DatabaseName ?? mongoUrl.DatabaseName ?? "NLog";
                var database = client.GetDatabase(databaseName);

                string collectionName = CollectionName ?? "Log";
                if (!CappedCollectionSize.HasValue || CollectionExists(database, collectionName))
                    return database.GetCollection<BsonDocument>(collectionName);

                // create capped
                var options = new CreateCollectionOptions
                {
                    Capped = true,
                    MaxSize = CappedCollectionSize,
                    MaxDocuments = CappedCollectionMaxItems
                };

                database.CreateCollection(collectionName, options);

                return database.GetCollection<BsonDocument>(collectionName);
            });
        }


        private static string GetConnectionString(string connectionName)
        {
            if (connectionName == null)
                throw new ArgumentNullException(nameof(connectionName));

            var settings = ConfigurationManager.ConnectionStrings[connectionName];
            if (settings == null)
                throw new NLogConfigurationException($"No connection string named '{connectionName}' could be found in the application configuration file.");

            string connectionString = settings.ConnectionString;
            if (string.IsNullOrEmpty(connectionString))
                throw new NLogConfigurationException($"The connection string '{connectionName}' in the application's configuration file does not contain the required connectionString attribute.");

            return settings.ConnectionString;
        }

        private static bool CollectionExists(IMongoDatabase database, string collectionName)
        {
            var options = new ListCollectionsOptions
            {
                Filter = Builders<BsonDocument>.Filter.Eq("name", collectionName)
            };

            return database.ListCollections(options).ToEnumerable().Any();
        }
    }
}
