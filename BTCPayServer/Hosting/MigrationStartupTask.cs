using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Services;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace BTCPayServer.Hosting
{
    public class MigrationStartupTask : IStartupTask
    {
        private readonly ApplicationDbContextFactory _DBContextFactory;
        private readonly StoreRepository _StoreRepository;
        private readonly BTCPayNetworkProvider _NetworkProvider;
        private readonly SettingsRepository _Settings;
        private readonly UserManager<ApplicationUser> _userManager;
        public MigrationStartupTask(
            BTCPayNetworkProvider networkProvider,
            StoreRepository storeRepository,
            ApplicationDbContextFactory dbContextFactory,
            UserManager<ApplicationUser> userManager,
            SettingsRepository settingsRepository)
        {
            _DBContextFactory = dbContextFactory;
            _StoreRepository = storeRepository;
            _NetworkProvider = networkProvider;
            _Settings = settingsRepository;
            _userManager = userManager;
        }
        public async Task ExecuteAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await Migrate(cancellationToken);
                var settings = (await _Settings.GetSettingAsync<MigrationSettings>()) ?? new MigrationSettings();
                if (!settings.StoreEventSignerCreatedCheck)
                {
                    await StoreEventSignerCreatedCheck();
                    settings.StoreEventSignerCreatedCheck = true;
                    await _Settings.UpdateSetting(settings);
                }       
                if (!settings.DeprecatedLightningConnectionStringCheck)
                {
                    await DeprecatedLightningConnectionStringCheck();
                    settings.DeprecatedLightningConnectionStringCheck = true;
                    await _Settings.UpdateSetting(settings);
                }
                if (!settings.UnreachableStoreCheck)
                {
                    await UnreachableStoreCheck();
                    settings.UnreachableStoreCheck = true;
                    await _Settings.UpdateSetting(settings);
                }
                if (!settings.ConvertMultiplierToSpread)
                {
                    await ConvertMultiplierToSpread();
                    settings.ConvertMultiplierToSpread = true;
                    await _Settings.UpdateSetting(settings);
                }
                if (!settings.ConvertNetworkFeeProperty)
                {
                    await ConvertNetworkFeeProperty();
                    settings.ConvertNetworkFeeProperty = true;
                    await _Settings.UpdateSetting(settings);
                }
                if (!settings.ConvertCrowdfundOldSettings)
                {
                    await ConvertCrowdfundOldSettings();
                    settings.ConvertCrowdfundOldSettings = true;
                    await _Settings.UpdateSetting(settings);
                }
                if (!settings.ConvertWalletKeyPathRoots)
                {
                    await ConvertConvertWalletKeyPathRoots();
                    settings.ConvertWalletKeyPathRoots = true;
                    await _Settings.UpdateSetting(settings);
                }
                if (!settings.CheckedFirstRun)
                {
                    var themeSettings = await _Settings.GetSettingAsync<ThemeSettings>() ?? new ThemeSettings();
                    var admin = await _userManager.GetUsersInRoleAsync(Roles.ServerAdmin);
                    themeSettings.FirstRun = admin.Count == 0;
                    await _Settings.UpdateSetting(themeSettings);
                    settings.CheckedFirstRun = true;
                    await _Settings.UpdateSetting(settings);
                }
            }
            catch (Exception ex)
            {
                Logs.PayServer.LogError(ex, "Error on the MigrationStartupTask");
                throw;
            }
        }

        private async Task Migrate(CancellationToken cancellationToken)
        {
            using (CancellationTokenSource timeout = new CancellationTokenSource(10_000))
            using (CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken))
            {
retry:
                try
                {
                    await _DBContextFactory.CreateContext().Database.MigrateAsync();
                }
                // Starting up
                catch when (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(1000, cts.Token);
                    }
                    catch { }
                    goto retry;
                }
            }
        }

        private async Task ConvertConvertWalletKeyPathRoots()
        {
            bool save = false;
            using (var ctx = _DBContextFactory.CreateContext())
            {
                foreach (var store in await ctx.Stores.AsQueryable().ToArrayAsync())
                {
#pragma warning disable CS0618 // Type or member is obsolete
                    var blob = store.GetStoreBlob();
                    if (blob.WalletKeyPathRoots == null)
                        continue;
                    foreach (var scheme in store.GetSupportedPaymentMethods(_NetworkProvider).OfType<DerivationSchemeSettings>())
                    {
                        if (blob.WalletKeyPathRoots.TryGetValue(scheme.PaymentId.ToString().ToLowerInvariant(), out var root))
                        {
                            scheme.AccountKeyPath = new NBitcoin.KeyPath(root);
                            store.SetSupportedPaymentMethod(scheme);
                            save = true;
                        }
                    }
                    blob.WalletKeyPathRoots = null;
                    store.SetStoreBlob(blob);
#pragma warning restore CS0618 // Type or member is obsolete
                }
                if (save)
                    await ctx.SaveChangesAsync();
            }
        }

        private async Task ConvertCrowdfundOldSettings()
        {
            using (var ctx = _DBContextFactory.CreateContext())
            {
                foreach (var app in await ctx.Apps.Where(a => a.AppType == "Crowdfund").ToArrayAsync())
                {
                    var settings = app.GetSettings<Services.Apps.CrowdfundSettings>();
#pragma warning disable CS0618 // Type or member is obsolete
                    if (settings.UseAllStoreInvoices)
#pragma warning restore CS0618 // Type or member is obsolete
                    {
                        app.TagAllInvoices = true;
                    }
                }
                await ctx.SaveChangesAsync();
            }
        }

        private async Task ConvertNetworkFeeProperty()
        {
            using (var ctx = _DBContextFactory.CreateContext())
            {
                foreach (var store in await ctx.Stores.AsQueryable().ToArrayAsync())
                {
                    var blob = store.GetStoreBlob();
#pragma warning disable CS0618 // Type or member is obsolete
                    if (blob.NetworkFeeDisabled != null)
                    {
                        blob.NetworkFeeMode = blob.NetworkFeeDisabled.Value ? NetworkFeeMode.Never : NetworkFeeMode.Always;
                        blob.NetworkFeeDisabled = null;
                        store.SetStoreBlob(blob);
                    }
#pragma warning restore CS0618 // Type or member is obsolete
                }
                await ctx.SaveChangesAsync();
            }
        }

        private async Task ConvertMultiplierToSpread()
        {
            using (var ctx = _DBContextFactory.CreateContext())
            {
                foreach (var store in await ctx.Stores.AsQueryable().ToArrayAsync())
                {
                    var blob = store.GetStoreBlob();
#pragma warning disable CS0612 // Type or member is obsolete
                    decimal multiplier = 1.0m;
                    if (blob.RateRules != null && blob.RateRules.Count != 0)
                    {
                        foreach (var rule in blob.RateRules)
                        {
                            multiplier = rule.Apply(null, multiplier);
                        }
                    }
                    blob.RateRules = null;
                    blob.Spread = Math.Min(1.0m, Math.Max(0m, -(multiplier - 1.0m)));
                    store.SetStoreBlob(blob);
#pragma warning restore CS0612 // Type or member is obsolete
                }
                await ctx.SaveChangesAsync();
            }
        }

        private Task UnreachableStoreCheck()
        {
            return _StoreRepository.CleanUnreachableStores();
        }

        private async Task DeprecatedLightningConnectionStringCheck()
        {
            using (var ctx = _DBContextFactory.CreateContext())
            {
                foreach (var store in await ctx.Stores.AsQueryable().ToArrayAsync())
                {
                    foreach (var method in store.GetSupportedPaymentMethods(_NetworkProvider).OfType<Payments.Lightning.LightningSupportedPaymentMethod>())
                    {
                        var lightning = method.GetLightningUrl();
                        if (lightning.IsLegacy)
                        {
                            method.SetLightningUrl(lightning);
                            store.SetSupportedPaymentMethod(method);
                        }
                    }
                }
                await ctx.SaveChangesAsync();
            }
        }

        private async Task StoreEventSignerCreatedCheck()
        {
            await using var ctx = _DBContextFactory.CreateContext();
            foreach (var store in await ctx.Stores.ToArrayAsync())
            {
                var blob = store.GetStoreBlob();
                blob.EventSigner = new Key();
                store.SetStoreBlob(blob);
            }
            await ctx.SaveChangesAsync();
        }
    }
}
