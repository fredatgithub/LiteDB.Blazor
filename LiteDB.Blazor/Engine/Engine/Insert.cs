﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

using static LiteDB.Constants;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        /// <summary>
        /// Insert all documents in collection. If document has no _id, use AutoId generation.
        /// </summary>
        public async Task<int> InsertAsync(string collection, IEnumerable<BsonDocument> docs, BsonAutoId autoId)
        {
            if (collection.IsNullOrWhiteSpace()) throw new ArgumentNullException(nameof(collection));
            if (docs == null) throw new ArgumentNullException(nameof(docs));

            return await this.AutoTransaction(async transaction =>
            {
                var snapshot = await transaction.CreateSnapshot(LockMode.Write, collection, true);
                var count = 0;
                var indexer = new IndexService(snapshot, _header.Pragmas.Collation);
                var data = new DataService(snapshot);

                LOG($"insert `{collection}`", "COMMAND");

                foreach (var doc in docs)
                {
                    await transaction.Safepoint();

                    await this.InsertDocumentAsync(snapshot, doc, autoId, indexer, data);

                    count++;
                }

                return count;
            });
        }

        /// <summary>
        /// Internal implementation of insert a document
        /// </summary>
        private async Task InsertDocumentAsync(Snapshot snapshot, BsonDocument doc, BsonAutoId autoId, IndexService indexer, DataService data)
        {
            // if no _id, use AutoId
            if (!doc.TryGetValue("_id", out var id))
            {
                doc["_id"] = id =
                    autoId == BsonAutoId.ObjectId ? new BsonValue(ObjectId.NewObjectId()) :
                    autoId == BsonAutoId.Guid ? new BsonValue(Guid.NewGuid()) :
                    this.GetSequence(snapshot, autoId);
            }
            else if(id.IsNumber)
            {
                // update memory sequence of numeric _id
                this.SetSequence(snapshot, id);
            }

            // test if _id is a valid type
            if (id.IsNull || id.IsMinValue || id.IsMaxValue)
            {
                throw LiteException.InvalidDataType("_id", id);
            }

            // storage in data pages - returns dataBlock address
            var dataBlock = await data.Insert(doc);

            IndexNode last = null;

            // for each index, insert new IndexNode
            foreach (var index in snapshot.CollectionPage.GetCollectionIndexes())
            {
                // for each index, get all keys (supports multi-key) - gets distinct values only
                // if index are unique, get single key only
                var keys = index.BsonExpr.GetIndexKeys(doc, _header.Pragmas.Collation);

                // do a loop with all keys (multi-key supported)
                foreach(var key in keys)
                {
                    // insert node
                    var node = await indexer.AddNode(index, key, dataBlock, last);

                    last = node;
                }
            }
        }
    }
}