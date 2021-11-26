﻿using System;
using Lykke.Common.Chaos;
using Lykke.SettingsReader.Attributes;

namespace Lykke.Service.Stellar.Api.Core.Settings.ServiceSettings
{
    public class StellarApiSettings
    {
        public DbSettings Db { get; set; }

        public TimeSpan TransactionExpirationTime { get; set; }

        public string NetworkPassphrase { get; set; }

        [HttpCheck("/")]
        public string HorizonUrl { get; set; }

        public string DepositBaseAddress { get; set; }

        public string[] ExplorerUrlFormats { get; set; }

        public AssetSettings NativeAsset { get; set; }
        
        [Optional]
        public ChaosSettings ChaosKitty { get; set; }

        [Optional]
        public uint OperationFee { get; set; } = 100U;
    }
}
