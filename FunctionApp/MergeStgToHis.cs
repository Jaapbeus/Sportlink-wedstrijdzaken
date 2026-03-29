using System;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using static SportlinkFunction.SystemUtilities;

namespace SportlinkFunction
{
    public class MergeStgToHis
    {
        private readonly string _sourceSchema;
        private readonly string _sourceTable;
        private readonly string _targetSchema;
        private readonly string _targetTable;

        public MergeStgToHis(string sourceSchema, string sourceTable, string targetSchema, string targetTable)
        {
            _sourceSchema = sourceSchema;
            _sourceTable = sourceTable;
            _targetSchema = targetSchema;
            _targetTable = targetTable;
        }

        public async Task ExecuteAsync(ILogger log)
        {
            try
            {
                using (SqlConnection connection = new SqlConnection(DatabaseConfig.ConnectionString))
                {
                    await connection.OpenAsync();
                    string query = $@"
                        EXECUTE [dbo].[sp_CreateTargetTableFromSource] '{_sourceSchema}','{_sourceTable}', '{_targetSchema}', '{_targetTable}';
                        EXECUTE [dbo].[sp_MergeStgToHis] '{_sourceSchema}','{_sourceTable}', '{_targetSchema}', '{_targetTable}';";

                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        await command.ExecuteNonQueryAsync();
                    }
                    connection.Close(); 
                    log.LogInformation($"{_sourceTable.ToUpper()} - Merged into {_targetTable.ToUpper()} table");
                }
            }
            catch (Exception ex)
            {
                log.LogError($"Error: {ex.Message}");
            }
        }
    }
}

