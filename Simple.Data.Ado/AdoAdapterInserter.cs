﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
using Simple.Data.Extensions;

namespace Simple.Data.Ado
{
    using Schema;

    class AdoAdapterInserter
    {
        private readonly AdoAdapter _adapter;
        private readonly IDbConnection _connection;
        private readonly IDbTransaction _transaction;

        public AdoAdapterInserter(AdoAdapter adapter) : this(adapter, (IDbTransaction)null)
        {
        }

        public AdoAdapterInserter(AdoAdapter adapter, IDbConnection connection)
        {
            _adapter = adapter;
            _connection = connection;
        }

        public AdoAdapterInserter(AdoAdapter adapter, IDbTransaction transaction)
        {
            _adapter = adapter;
            _transaction = transaction;
            if (transaction != null) _connection = transaction.Connection;
        }

        public IEnumerable<IDictionary<string, object>> InsertMany(string tableName, IEnumerable<IDictionary<string, object>> data, Func<IDictionary<string,object>, Exception, bool> onError, bool resultRequired)
        {
            if (data == null) throw new ArgumentNullException("data");
            var list = data.ToList();
            var table = _adapter.GetSchema().FindTable(tableName);
            foreach (var row in list)
            {
                CheckInsertablePropertiesAreAvailable(table, row);
            }

            var bulkInserter = _adapter.ProviderHelper.GetCustomProvider<IBulkInserter>(_adapter.ConnectionProvider) ?? new BulkInserter();
            return bulkInserter.Insert(_adapter, tableName, list, _transaction, onError, resultRequired);
        }

        public IDictionary<string, object> Insert(string tableName, IEnumerable<KeyValuePair<string, object>> data, bool resultRequired)
        {
            var table = _adapter.GetSchema().FindTable(tableName);

            CheckInsertablePropertiesAreAvailable(table, data);

            var customInserter = _adapter.ProviderHelper.GetCustomProvider<ICustomInserter>(_adapter.ConnectionProvider);
            if (customInserter != null)
            {
                return customInserter.Insert(_adapter, tableName, data.ToDictionary(), _transaction);
            }

            var dataDictionary = data.Where(kvp => table.HasColumn(kvp.Key) && !table.FindColumn(kvp.Key).IsIdentity)
                                     .ToDictionary(kvp => table.FindColumn(kvp.Key), kvp => kvp.Value);

            string columnList = dataDictionary.Keys.Select(c => c.QuotedName).Aggregate((agg, next) => agg + "," + next);
            string valueList = dataDictionary.Keys.Select(s => "?").Aggregate((agg, next) => agg + "," + next);

            string insertSql = "insert into " + table.QualifiedName + " (" + columnList + ") values (" + valueList + ")";

            if (resultRequired)
            {
                var identityFunction = _adapter.GetIdentityFunction();
                if (!string.IsNullOrWhiteSpace(identityFunction))
                {
                    var identityColumn = table.Columns.FirstOrDefault(col => col.IsIdentity);

                    if (identityColumn != null)
                    {
                        var selectSql = "select * from " + table.QualifiedName + " where " + identityColumn.QuotedName +
                                        " = " + identityFunction;
                        if (_adapter.ProviderSupportsCompoundStatements)
                        {
                            insertSql += "; " + selectSql;
                            return ExecuteSingletonQuery(insertSql, dataDictionary.Keys, dataDictionary.Values);
                        }
                        else
                        {
                            return ExecuteSingletonQuery(insertSql, selectSql, dataDictionary.Keys,
                                                         dataDictionary.Values);
                        }
                    }
                }
            }

            Execute(insertSql, dataDictionary.Keys, dataDictionary.Values);
            return null;
        }

        private void CheckInsertablePropertiesAreAvailable(Table table, IEnumerable<KeyValuePair<string, object>> data)
        {
            data = data.Where(kvp => table.HasColumn(kvp.Key));

            if (!data.Any())
            {
                throw new SimpleDataException("No properties were found which could be mapped to the database.");
            }
        }

        internal IDictionary<string, object> ExecuteSingletonQuery(string sql, IEnumerable<Column> columns, IEnumerable<Object> values)
        {
            if (_transaction != null)
            {
                var command = new CommandHelper(_adapter).CreateInsert(_transaction.Connection, sql, columns, values.ToArray());
                command.Transaction = _transaction;
                return TryExecuteSingletonQuery(command);
            }

            var connection = _connection ?? _adapter.CreateConnection();
            using (connection.MaybeDisposable())
            {
                using (var command = new CommandHelper(_adapter).CreateInsert(connection, sql, columns, values.ToArray()))
                {
                    connection.OpenIfClosed();
                    return TryExecuteSingletonQuery(command);
                }
            }
        }

        internal IDictionary<string, object> ExecuteSingletonQuery(string insertSql, string selectSql, IEnumerable<Column> columns, IEnumerable<Object> values)
        {
            if (_transaction != null)
            {
                var command = new CommandHelper(_adapter).CreateInsert(_transaction.Connection, insertSql, columns, values.ToArray());
                command.Transaction = _transaction;
                TryExecute(command);
                command.CommandText = selectSql;
                command.Parameters.Clear();
                return TryExecuteSingletonQuery(command);
            }

            var connection = _connection ?? _adapter.CreateConnection();
            using (connection.MaybeDisposable())
            {
                using (var command = new CommandHelper(_adapter).CreateInsert(connection, insertSql, columns, values.ToArray()))
                {
                    connection.OpenIfClosed();
                    TryExecute(command);
                    command.CommandText = selectSql;
                    command.Parameters.Clear();
                    return TryExecuteSingletonQuery(command);
                }
            }
        }

        private static IDictionary<string, object> TryExecuteSingletonQuery(IDbCommand command)
        {
            command.WriteTrace();
            try
            {
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        return reader.ToDictionary();
                    }
                }
            }
            catch (DbException ex)
            {
                throw new AdoAdapterException(ex.Message, command);
            }

            return null;
        }

        internal int Execute(string sql, IEnumerable<Column> columns, IEnumerable<Object> values)
        {
            if (_transaction != null)
            {
                var command = new CommandHelper(_adapter).CreateInsert(_transaction.Connection, sql, columns, values.ToArray());
                command.Transaction = _transaction;
                return TryExecute(command);
            }
            var connection = _connection ?? _adapter.CreateConnection();
            using (connection.MaybeDisposable())
            {
                using (var command = new CommandHelper(_adapter).CreateInsert(connection, sql, columns, values.ToArray()))
                {
                    connection.OpenIfClosed();
                    return TryExecute(command);
                }
            }
        }

        private static int TryExecute(IDbCommand command)
        {
            command.WriteTrace();
            try
            {
                return command.ExecuteNonQuery();
            }
            catch (DbException ex)
            {
                throw new AdoAdapterException(ex.Message, command);
            }
        }
    }
}
