﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using static LiteDB.Constants;

namespace LiteDB.Engine
{
    public partial class LiteEngine
    {
        /// <summary>
        /// Initialize a new transaction. Transaction are created "per-thread". There is only one single transaction per thread.
        /// Return true if transaction was created or false if current thread already in a transaction.
        /// </summary>
        public async Task<bool> BeginTrans()
        {
            var isNew = _transaction == null;
            var transacion = await this.GetTransaction(false);

            transacion.ExplicitTransaction = true;

            if (transacion.OpenCursors.Count > 0) throw new LiteException(0, "This thread contains an open cursors/query. Close cursors before Begin()");

            LOG(isNew, $"begin trans", "COMMAND");

            return isNew;
        }

        /// <summary>
        /// Persist all dirty pages into LOG file
        /// </summary>
        public async Task<bool> Commit()
        {
            if (_transaction != null)
            {
                // do not accept explicit commit transaction when contains open cursors running
                if (_transaction.OpenCursors.Count > 0) throw new LiteException(0, "Current transaction contains open cursors. Close cursors before run Commit()");

                if (_transaction.State == TransactionState.Active)
                {
                    // persist transaction
                    await _transaction.Commit();

                    // release transaction lock
                    _locker.Release();

                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Do rollback to current transaction. Clear dirty pages in memory and return new pages to main empty linked-list
        /// </summary>
        public async Task<bool> Rollback()
        {
            if (_transaction == null) return false;

            if (_transaction != null && _transaction.State == TransactionState.Active)
            {
                // discard changes
                await _transaction.Rollback();

                // release lock
                _locker.Release();

                return true;
            }

            return false;
        }

        /// <summary>
        /// Get/Set current transaction
        /// </summary>
        private TransactionService _transaction = null;

        /// <summary>
        /// Get current transaction or create a new one
        /// </summary>
        private async Task<TransactionService> GetTransaction(bool queryOnly)
        {
            if (_transaction == null)
            {
                // lock transaction
                await _locker.WaitAsync(_header.Pragmas.Timeout);

                _transaction = new TransactionService(_header, _disk, _walIndex, MAX_TRANSACTION_SIZE, queryOnly);
            }

            return _transaction;
        }

        /// <summary>
        /// Create (or reuse) a transaction an add try/catch block. Commit transaction if is new transaction
        /// </summary>
        private async Task<T> AutoTransaction<T>(Func<TransactionService, Task<T>> fn)
        {
            var isNew = _transaction == null;
            var transaction = await this.GetTransaction(false);

            try
            {
                var result = await fn(transaction);

                await transaction.Commit();

                // if this transaction was auto-created for this operation, commit & dispose now
                if (isNew)
                {
                    if (_header.Pragmas.Checkpoint > 0 &&
                        transaction.Mode == LockMode.Write &&
                        _disk.LogLength > (_header.Pragmas.Checkpoint * PAGE_SIZE))
                    {
                        await _walIndex.Checkpoint();
                    }
                }

                return result;
            }
            catch(Exception ex)
            {
                LOG(ex.Message, "ERROR");

                await transaction.Rollback();

                _transaction = null;

                throw;
            }
        }
    }
}