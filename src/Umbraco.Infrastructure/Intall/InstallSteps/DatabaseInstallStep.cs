﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Umbraco.Core;
using Umbraco.Core.Configuration;
using Umbraco.Core.IO;
using Umbraco.Core.Logging;
using Umbraco.Core.Migrations.Install;
using Umbraco.Web.Install.Models;

namespace Umbraco.Web.Install.InstallSteps
{
    [InstallSetupStep(InstallationType.NewInstall | InstallationType.Upgrade,
        "DatabaseInstall", 11, "")]
    public class DatabaseInstallStep : InstallSetupStep<object>
    {
        private readonly DatabaseBuilder _databaseBuilder;
        private readonly IRuntimeState _runtime;
        private readonly ILogger _logger;
        private readonly IIOHelper _ioHelper;
        private readonly IConnectionStrings _connectionStrings;

        public DatabaseInstallStep(DatabaseBuilder databaseBuilder, IRuntimeState runtime, ILogger logger, IIOHelper ioHelper, IConnectionStrings connectionStrings)
        {
            _databaseBuilder = databaseBuilder;
            _runtime = runtime;
            _logger = logger;
            _ioHelper = ioHelper;
            _connectionStrings = connectionStrings;
        }

        public override Task<InstallSetupResult> ExecuteAsync(object model)
        {
            if (_runtime.Level == RuntimeLevel.Run)
                throw new Exception("Umbraco is already configured!");

            var result = _databaseBuilder.CreateSchemaAndData();

            if (result.Success == false)
            {
                throw new InstallException("The database failed to install. ERROR: " + result.Message);
            }

            if (result.RequiresUpgrade == false)
            {
                HandleConnectionStrings(_logger, _ioHelper, _connectionStrings);
                return Task.FromResult<InstallSetupResult>(null);
            }

            //upgrade is required so set the flag for the next step
            return Task.FromResult(new InstallSetupResult(new Dictionary<string, object>
            {
                {"upgrade", true}
            }));
        }

        internal static void HandleConnectionStrings(ILogger logger, IIOHelper ioHelper, IConnectionStrings connectionStrings)
        {


            var databaseSettings = connectionStrings[Constants.System.UmbracoConnectionName];



            // Remove legacy umbracoDbDsn configuration setting if it exists and connectionstring also exists
            if (databaseSettings != null)
            {
                connectionStrings.RemoveConnectionString(Constants.System.UmbracoConnectionName, ioHelper);
            }
            else
            {
                var ex = new ArgumentNullException(string.Format("ConfigurationManager.ConnectionStrings[{0}]", Constants.System.UmbracoConnectionName), "Install / upgrade did not complete successfully, umbracoDbDSN was not set in the connectionStrings section");
                logger.Error<DatabaseInstallStep>(ex, "Install / upgrade did not complete successfully, umbracoDbDSN was not set in the connectionStrings section");
                throw ex;
            }
        }

        public override bool RequiresExecution(object model)
        {
            return true;
        }
    }
}