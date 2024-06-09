﻿using D4Companion.Entities;
using D4Companion.Events;
using D4Companion.Helpers;
using D4Companion.Interfaces;
using FuzzierSharp;
using FuzzierSharp.SimilarityRatio;
using FuzzierSharp.SimilarityRatio.Scorer.Composite;
using FuzzierSharp.SimilarityRatio.Scorer.StrategySensitive;
using Microsoft.Extensions.Logging;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using Prism.Events;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace D4Companion.Services
{
    public class BuildsManagerMobalytics : IBuildsManagerMobalytics
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly ILogger _logger;
        private readonly IAffixManager _affixManager;
        private readonly ISettingsManager _settingsManager;

        private static readonly int _delayClick = 500;

        private List<AffixInfo> _affixes = new List<AffixInfo>();
        private List<string> _affixDescriptions = new List<string>();
        private Dictionary<string, string> _affixMapDescriptionToId = new Dictionary<string, string>();
        private List<AspectInfo> _aspects = new List<AspectInfo>();
        private List<string> _aspectNames = new List<string>();
        private Dictionary<string, string> _aspectMapNameToId = new Dictionary<string, string>();
        private List<MobalyticsBuild> _mobalyticsBuilds = new();
        private WebDriver? _webDriver = null;
        private WebDriverWait? _webDriverWait = null;

        // Start of Constructors region

        #region Constructors

        public BuildsManagerMobalytics(IEventAggregator eventAggregator, ILogger<BuildsManagerMobalytics> logger, IAffixManager affixManager, ISettingsManager settingsManager)
        {
            // Init IEventAggregator
            _eventAggregator = eventAggregator;

            // Init logger
            _logger = logger;

            // Init services
            _affixManager = affixManager;
            _settingsManager = settingsManager;

            // Init data
            InitAffixData();
            InitAspectData();

            // Load available Mobalytics builds.
            Task.Factory.StartNew(() =>
            {
                LoadAvailableMobalyticsBuilds();
            });
        }

        #endregion

        // Start of Events region

        #region Events

        #endregion

        // Start of Properties region

        #region Properties

        public List<MobalyticsBuild> MobalyticsBuilds { get => _mobalyticsBuilds; set => _mobalyticsBuilds = value; }

        #endregion

        // Start of Event handlers region

        #region Event handlers

        #endregion

        // Start of Methods region

        #region Methods

        private void InitAffixData()
        {
            _affixes.Clear();
            string resourcePath = @".\Data\Affixes.enUS.json";
            using (FileStream? stream = File.OpenRead(resourcePath))
            {
                if (stream != null)
                {
                    // create the options
                    var options = new JsonSerializerOptions()
                    {
                        WriteIndented = true
                    };
                    // register the converter
                    options.Converters.Add(new BoolConverter());
                    options.Converters.Add(new IntConverter());

                    _affixes = JsonSerializer.Deserialize<List<AffixInfo>>(stream, options) ?? new List<AffixInfo>();
                }
            }

            // Create affix description list for FuzzierSharp
            _affixDescriptions.Clear();
            _affixDescriptions = _affixes.Select(affix =>
            {
                // Remove class restrictions from description. Mobalytics does not show this information.
                return affix.DescriptionClean.Contains(")") ? affix.DescriptionClean.Split(new char[] { '(', ')' }, StringSplitOptions.RemoveEmptyEntries)[0] : affix.DescriptionClean;
            }).ToList();

            // Create dictionary to map affix description with affix id
            _affixMapDescriptionToId.Clear();
            _affixMapDescriptionToId = _affixes.ToDictionary(affix =>
            {
                // Remove class restrictions from description. Mobalytics does not show this information.
                return affix.DescriptionClean.Contains(")") ? affix.DescriptionClean.Split(new char[] { '(', ')' }, StringSplitOptions.RemoveEmptyEntries)[0] : affix.DescriptionClean;
            }, affix => affix.IdName);
        }

        private void InitAspectData()
        {
            _aspects.Clear();
            string resourcePath = @".\Data\Aspects.enUS.json";
            using (FileStream? stream = File.OpenRead(resourcePath))
            {
                if (stream != null)
                {
                    // create the options
                    var options = new JsonSerializerOptions()
                    {
                        WriteIndented = true
                    };
                    // register the converter
                    options.Converters.Add(new BoolConverter());
                    options.Converters.Add(new IntConverter());

                    _aspects = JsonSerializer.Deserialize<List<AspectInfo>>(stream, options) ?? new List<AspectInfo>();
                }
            }

            // Create aspect name list for FuzzierSharp
            _aspectNames.Clear();
            _aspectNames = _aspects.Select(aspect => aspect.Name).ToList();

            // Create dictionary to map aspect name with aspect id
            _aspectMapNameToId.Clear();
            _aspectMapNameToId = _aspects.ToDictionary(aspect => aspect.Name, aspect => aspect.IdName);
        }

        private void InitSelenium()
        {
            // Options: Headless, size, security, ...
            var options = new ChromeOptions();

            options.AddArgument("--headless");
            options.AddArgument("--disable-gpu"); // Applicable to windows os only

            options.AddArgument("--disable-extensions");
            options.AddArgument("--disable-popup-blocking");
            options.AddArgument("--disable-notifications");
            options.AddArgument("--dns-prefetch-disable");
            options.AddArgument("--disable-dev-shm-usage"); // Overcome limited resource problems
            options.AddArgument("--no-sandbox"); // Bypass OS security model
            options.AddArgument("--window-size=1600,900");

            // Service
            ChromeDriverService service = ChromeDriverService.CreateDefaultService();
            service.HideCommandPromptWindow = true;

            // Create driver
            _webDriver = new ChromeDriver(service: service, options: options);
            _webDriverWait = new WebDriverWait(_webDriver, TimeSpan.FromSeconds(10));
        }

        public void CreatePresetFromMobalyticsBuild(MobalyticsBuildVariant mobalyticsBuild, string buildNameOriginal, string buildName)
        {
            buildName = string.IsNullOrWhiteSpace(buildName) ? buildNameOriginal : buildName;

            // Note: Only allow one Mobalytics build. Update if already exists.
            _affixManager.AffixPresets.RemoveAll(p => p.Name.Equals(buildName));

            var affixPreset = mobalyticsBuild.AffixPreset;
            affixPreset.Name = buildName;

            _affixManager.AddAffixPreset(affixPreset);
        }

        public void DownloadMobalyticsBuild(string buildUrl)
        {
            string id = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(buildUrl));

            try
            {
                if (_webDriver == null) InitSelenium();

                MobalyticsBuild mobalyticsBuild = new MobalyticsBuild
                {
                    Id = id,
                    Url = buildUrl
                };

                _eventAggregator.GetEvent<MobalyticsStatusUpdateEvent>().Publish(new MobalyticsStatusUpdateEventParams { Build = mobalyticsBuild, Status = $"Downloading {mobalyticsBuild.Url}." });
                _webDriver.Navigate().GoToUrl(mobalyticsBuild.Url);

                try
                {
                    // Wait for cookies
                    var elementCookie = _webDriverWait.Until(e =>
                    {
                        var elements = _webDriver.FindElements(By.ClassName("qc-cmp2-summary-buttons"));
                        if (elements.Count > 0 && elements[0].Displayed)
                        {
                            return elements[0];
                        }
                        return null;
                    });

                    // Accept cookies
                    if (elementCookie != null)
                    {
                        //var asHtml = elementCookie.GetAttribute("innerHTML");
                        elementCookie.FindElements(By.TagName("button"))[1].Click();
                        Thread.Sleep(_delayClick);
                    }
                }
                catch (Exception)
                {
                    // No cookies when using "options.AddArgument("--headless");"
                }

                // Build name
                mobalyticsBuild.Name = GetBuildName();

                if (string.IsNullOrWhiteSpace(mobalyticsBuild.Name))
                {
                    _eventAggregator.GetEvent<MobalyticsStatusUpdateEvent>().Publish(new MobalyticsStatusUpdateEventParams { Build = new MobalyticsBuild { Id = id, Url = buildUrl }, Status = $"Failed - Build name not found." });
                }
                else
                {
                    _eventAggregator.GetEvent<MobalyticsStatusUpdateEvent>().Publish(new MobalyticsStatusUpdateEventParams { Build = mobalyticsBuild, Status = $"Downloaded {mobalyticsBuild.Name}." });

                    // Last update
                    mobalyticsBuild.Date = GetLastUpdateInfo();

                    // Variants
                    ExportBuildVariants(mobalyticsBuild);
                    ConvertBuildVariants(mobalyticsBuild);

                    // Save
                    Directory.CreateDirectory(@".\Builds\Mobalytics");
                    using (FileStream stream = File.Create(@$".\Builds\Mobalytics\{mobalyticsBuild.Id}.json"))
                    {
                        var options = new JsonSerializerOptions { WriteIndented = true };
                        JsonSerializer.Serialize(stream, mobalyticsBuild, options);
                    }
                    LoadAvailableMobalyticsBuilds();

                    _eventAggregator.GetEvent<MobalyticsStatusUpdateEvent>().Publish(new MobalyticsStatusUpdateEventParams { Build = mobalyticsBuild, Status = $"Done." });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"{MethodBase.GetCurrentMethod()?.Name} ({buildUrl})");

                _eventAggregator.GetEvent<ErrorOccurredEvent>().Publish(new ErrorOccurredEventParams
                {
                    Message = $"Failed to download from Mobalytics ({buildUrl})"
                });

                _eventAggregator.GetEvent<MobalyticsStatusUpdateEvent>().Publish(new MobalyticsStatusUpdateEventParams { Build = new MobalyticsBuild { Id = id, Url = buildUrl }, Status = $"Failed." });
            }
            finally
            {
                _webDriver?.Quit();
                _webDriver = null;
                _webDriverWait = null;

                _eventAggregator.GetEvent<MobalyticsCompletedEvent>().Publish();
            }
        }

        private void ConvertBuildVariants(MobalyticsBuild mobalyticsBuild)
        {
            foreach (var variant in mobalyticsBuild.Variants)
            {
                _eventAggregator.GetEvent<MobalyticsStatusUpdateEvent>().Publish(new MobalyticsStatusUpdateEventParams { Build = mobalyticsBuild, Status = $"Converting {variant.Name}." });

                var affixPreset = new AffixPreset
                {
                    Name = variant.Name
                };

                // Prepare affixes
                List<Tuple<string, string>> affixesMobalytics = new List<Tuple<string, string>>();

                foreach (var affixMobalytics in variant.Helm)
                {
                    affixesMobalytics.Add(new Tuple<string, string>(Constants.ItemTypeConstants.Helm, affixMobalytics));
                }
                foreach (var affixMobalytics in variant.Chest)
                {
                    affixesMobalytics.Add(new Tuple<string, string>(Constants.ItemTypeConstants.Chest, affixMobalytics));
                }
                foreach (var affixMobalytics in variant.Gloves)
                {
                    affixesMobalytics.Add(new Tuple<string, string>(Constants.ItemTypeConstants.Gloves, affixMobalytics));
                }
                foreach (var affixMobalytics in variant.Pants)
                {
                    affixesMobalytics.Add(new Tuple<string, string>(Constants.ItemTypeConstants.Pants, affixMobalytics));
                }
                foreach (var affixMobalytics in variant.Boots)
                {
                    affixesMobalytics.Add(new Tuple<string, string>(Constants.ItemTypeConstants.Boots, affixMobalytics));
                }
                foreach (var affixMobalytics in variant.Amulet)
                {
                    affixesMobalytics.Add(new Tuple<string, string>(Constants.ItemTypeConstants.Amulet, affixMobalytics));
                }
                foreach (var affixMobalytics in variant.Ring)
                {
                    affixesMobalytics.Add(new Tuple<string, string>(Constants.ItemTypeConstants.Ring, affixMobalytics));
                }
                foreach (var affixMobalytics in variant.Weapon)
                {
                    affixesMobalytics.Add(new Tuple<string, string>(Constants.ItemTypeConstants.Weapon, affixMobalytics));
                }
                foreach (var affixMobalytics in variant.Ranged)
                {
                    affixesMobalytics.Add(new Tuple<string, string>(Constants.ItemTypeConstants.Ranged, affixMobalytics));
                }
                foreach (var affixMobalytics in variant.Offhand)
                {
                    affixesMobalytics.Add(new Tuple<string, string>(Constants.ItemTypeConstants.Offhand, affixMobalytics));
                }

                // Find matching affix ids
                ConcurrentBag<ItemAffix> itemAffixBag = new ConcurrentBag<ItemAffix>();
                Parallel.ForEach(affixesMobalytics, affixMobalytics =>
                {
                    var itemAffixResult = ConvertItemAffix(affixMobalytics);
                    itemAffixBag.Add(itemAffixResult);
                });
                affixPreset.ItemAffixes.AddRange(itemAffixBag);

                // Sort affixes
                affixPreset.ItemAffixes.Sort((x, y) =>
                {
                    if (x.Id == y.Id && x.IsImplicit == y.IsImplicit && x.IsTempered == y.IsTempered) return 0;

                    int result = x.IsTempered && !y.IsTempered ? 1 : y.IsTempered && !x.IsTempered ? -1 : 0;
                    if (result == 0)
                    {
                        result = x.IsImplicit && !y.IsImplicit ? -1 : y.IsImplicit && !x.IsImplicit ? 1 : 0;
                    }

                    return result;
                });

                // Remove duplicates
                affixPreset.ItemAffixes = affixPreset.ItemAffixes.DistinctBy(a => new { a.Id, a.Type }).ToList();

                // Find matching aspect ids
                ConcurrentBag<ItemAffix> itemAspectBag = new ConcurrentBag<ItemAffix>();
                Parallel.ForEach(variant.Aspect, aspect =>
                {
                    var itemAspectResult = ConvertItemAspect(aspect);
                    itemAspectBag.Add(itemAspectResult);
                });
                foreach (var aspect in itemAspectBag)
                {
                    affixPreset.ItemAspects.Add(new ItemAffix { Id = aspect.Id, Type = Constants.ItemTypeConstants.Helm });
                    affixPreset.ItemAspects.Add(new ItemAffix { Id = aspect.Id, Type = Constants.ItemTypeConstants.Chest });
                    affixPreset.ItemAspects.Add(new ItemAffix { Id = aspect.Id, Type = Constants.ItemTypeConstants.Gloves });
                    affixPreset.ItemAspects.Add(new ItemAffix { Id = aspect.Id, Type = Constants.ItemTypeConstants.Pants });
                    affixPreset.ItemAspects.Add(new ItemAffix { Id = aspect.Id, Type = Constants.ItemTypeConstants.Boots });
                    affixPreset.ItemAspects.Add(new ItemAffix { Id = aspect.Id, Type = Constants.ItemTypeConstants.Amulet });
                    affixPreset.ItemAspects.Add(new ItemAffix { Id = aspect.Id, Type = Constants.ItemTypeConstants.Ring });
                    affixPreset.ItemAspects.Add(new ItemAffix { Id = aspect.Id, Type = Constants.ItemTypeConstants.Weapon });
                    affixPreset.ItemAspects.Add(new ItemAffix { Id = aspect.Id, Type = Constants.ItemTypeConstants.Ranged });
                    affixPreset.ItemAspects.Add(new ItemAffix { Id = aspect.Id, Type = Constants.ItemTypeConstants.Offhand });
                }

                variant.AffixPreset = affixPreset;
                _eventAggregator.GetEvent<MobalyticsStatusUpdateEvent>().Publish(new MobalyticsStatusUpdateEventParams { Build = mobalyticsBuild, Status = $"Converted {variant.Name}." });
            }
        }

        private ItemAffix ConvertItemAffix(Tuple<string, string> affixMobalytics)
        {
            string affixId = string.Empty;
            string itemType = affixMobalytics.Item1;

            // Clean string for tempered affixes
            string affixClean = affixMobalytics.Item2.Contains(":") ? affixMobalytics.Item2.Split(new char[] { ':' }, StringSplitOptions.RemoveEmptyEntries)[1] : affixMobalytics.Item2;

            // Clean string
            affixClean = affixClean.Trim();

            var result = Process.ExtractOne(affixClean, _affixDescriptions, scorer: ScorerCache.Get<DefaultRatioScorer>());
            affixId = _affixMapDescriptionToId[result.Value];

            bool isTempered = affixMobalytics.Item2.Contains(":");

            return new ItemAffix
            {
                Id = affixId,
                Type = itemType,
                IsTempered = isTempered
            };
        }

        private ItemAffix ConvertItemAspect(string aspect)
        {
            string aspectId = string.Empty;

            var result = Process.ExtractOne(aspect.Replace("Aspect", string.Empty, StringComparison.OrdinalIgnoreCase), _aspectNames, scorer: ScorerCache.Get<WeightedRatioScorer>());
            aspectId = _aspectMapNameToId[result.Value];

            return new ItemAffix
            {
                Id = aspectId,
                Type = Constants.ItemTypeConstants.Helm
            };
        }

        private void ExportBuildVariants(MobalyticsBuild mobalyticsBuild)
        {
            var elementMain = _webDriver.FindElement(By.TagName("main"));
            var elementMainContent = elementMain.FindElements(By.XPath("./div/div/div[1]/div"));
            var elementVariants = elementMainContent.FirstOrDefault(e =>
            {
                int count = e.FindElements(By.XPath("./div")).Count();
                if (count <= 1) return false;
                int countSpan = e.FindElements(By.XPath("./div[./span]")).Count();

                return count == countSpan;
            });

            // Website layout check - Single or multiple build layout.
            if (elementVariants == null)
            {
                ExportBuildVariant(mobalyticsBuild.Name, mobalyticsBuild);
            }
            else
            {
                var variants = elementVariants.FindElements(By.XPath("./div"));
                //var variantsAsHtml = elementVariants.FindElements(By.XPath("./div")).GetAttribute("innerHTML");
                foreach (var variant in variants)
                {
                    _ = _webDriver?.ExecuteScript("arguments[0].click();", variant);
                    Thread.Sleep(_delayClick);
                    ExportBuildVariant(variant.Text, mobalyticsBuild);
                }
            }
        }

        private void ExportBuildVariant(string variantName, MobalyticsBuild mobalyticsBuild)
        {
            // Set timeout to improve performance
            // https://stackoverflow.com/questions/16075997/iselementpresent-is-very-slow-in-case-if-element-does-not-exist
            _webDriver.Manage().Timeouts().ImplicitWait = TimeSpan.FromMilliseconds(0);

            _eventAggregator.GetEvent<MobalyticsStatusUpdateEvent>().Publish(new MobalyticsStatusUpdateEventParams { Build = mobalyticsBuild, Status = $"Exporting {variantName}." });

            var mobalyticsBuildVariant = new MobalyticsBuildVariant
            {
                Name = variantName
            };

            // Look for aspect and gear stats container
            // "Aspects & Uniques"
            // "Gear Stats"
            string header = "Aspects & Uniques";
            var aspectAndGearStatsHeader = _webDriver.FindElement(By.XPath($"//header[./div[contains(text(), '{header}')]]")).FindElements(By.TagName("div"));
            
            // Aspects
            _ = _webDriver?.ExecuteScript("arguments[0].click();", aspectAndGearStatsHeader[0]);
            Thread.Sleep(_delayClick);
            mobalyticsBuildVariant.Aspect = GetAllAspects();

            // Gear Stats
            _ = _webDriver?.ExecuteScript("arguments[0].click();", aspectAndGearStatsHeader[1]);
            Thread.Sleep(_delayClick);

            // Armor
            mobalyticsBuildVariant.Helm = GetAllAffixes("Helm");
            mobalyticsBuildVariant.Chest = GetAllAffixes("Chest Armor");
            mobalyticsBuildVariant.Gloves = GetAllAffixes("Gloves");
            mobalyticsBuildVariant.Pants = GetAllAffixes("Pants");
            mobalyticsBuildVariant.Boots = GetAllAffixes("Boots");

            // Accessories
            mobalyticsBuildVariant.Amulet = GetAllAffixes("Amulet");
            mobalyticsBuildVariant.Ring.AddRange(GetAllAffixes("Ring 1"));
            mobalyticsBuildVariant.Ring.AddRange(GetAllAffixes("Ring 2"));
            mobalyticsBuildVariant.Ring = mobalyticsBuildVariant.Ring.Distinct().ToList();

            // Weapons
            mobalyticsBuildVariant.Weapon.AddRange(GetAllAffixes("Weapon"));
            mobalyticsBuildVariant.Weapon.AddRange(GetAllAffixes("Bludgeoning Weapon"));
            mobalyticsBuildVariant.Weapon.AddRange(GetAllAffixes("Slashing Weapon"));
            mobalyticsBuildVariant.Weapon.AddRange(GetAllAffixes("Dual-Wield Weapon 1"));
            mobalyticsBuildVariant.Weapon.AddRange(GetAllAffixes("Dual-Wield Weapon 2"));
            mobalyticsBuildVariant.Weapon = mobalyticsBuildVariant.Weapon.Distinct().ToList();
            mobalyticsBuildVariant.Ranged = GetAllAffixes("Ranged Weapon");
            mobalyticsBuildVariant.Offhand = GetAllAffixes("Offhand");

            mobalyticsBuild.Variants.Add(mobalyticsBuildVariant);
            _eventAggregator.GetEvent<MobalyticsStatusUpdateEvent>().Publish(new MobalyticsStatusUpdateEventParams { Build = mobalyticsBuild, Status = $"Exported {variantName}." });

            // Reset Timeout
            _webDriver.Manage().Timeouts().ImplicitWait = TimeSpan.FromMilliseconds(10 * 1000);
        }

        private List<string> GetAllAffixes(string itemType)
        {
            try
            {
                List<string> affixes = new List<string>();

                string header = "Gear Stats";
                var affixContainer = _webDriver.FindElement(By.XPath($"//div[./header[./div[contains(text(), '{header}')]]]"))
                    .FindElement(By.XPath(".//div/div[1]"))
                    .FindElements(By.XPath("div"));

                // Find element that contains the current itemType
                var affixContainerType = affixContainer.FirstOrDefault(e =>
                {
                    var elements = e.FindElements(By.XPath($".//div/div/div/span[1]"));
                    if (elements.Any())
                    {
                        return elements[0].Text.Equals(itemType);
                    }

                    return false;
                });

                if (affixContainerType != null)
                {
                    //var asHtml = affixContainerType.GetAttribute("innerHTML");

                    // Find the list items with affixes
                    var elementAffixes = affixContainerType.FindElements(By.TagName("li"));
                    foreach (var elementAffix in elementAffixes)
                    {
                        var elementSpans = elementAffix.FindElements(By.TagName("span"));
                        string affix = elementSpans.Count == 1 || (elementSpans.Count > 1 && string.IsNullOrWhiteSpace(elementSpans[1].Text)) ? elementSpans[0].Text :
                            elementSpans[0].Text.Replace(elementSpans[1].Text, string.Empty).Trim();

                        affixes.Add(affix);
                    }
                }
                return affixes;
            }
            catch (Exception)
            {
                return new();
            }
        }

        private List<string> GetAllAspects()
        {
            try
            {
                string header = "Aspects & Uniques";
                var aspectContainer = _webDriver.FindElement(By.XPath($"//div[./header[./div[contains(text(), '{header}')]]]"))
                    .FindElement(By.XPath(".//div/div[1]"))
                    .FindElements(By.XPath("div"));

                List<string> aspects = new List<string>();
                foreach (var aspect in aspectContainer)
                {
                    var description = aspect.Text.Split(new string[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in description)
                    {
                        if (line.Contains("Aspect", StringComparison.OrdinalIgnoreCase))
                        {
                            aspects.Add(line);
                            break;
                        }
                    }
                }
                return aspects;
            }
            catch (Exception)
            {
                return new();
            }
        }

        private string GetBuildName()
        {
            try
            {
                var container = _webDriver.FindElement(By.Id("container"));
                string buildDescription = container.FindElements(By.TagName("h1"))[0].Text;
                return buildDescription.Split(new string[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)[1];
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private string GetLastUpdateInfo()
        {
            try
            {
                var container = _webDriver.FindElement(By.Id("container"));
                string lastUpdateInfo = container.FindElements(By.TagName("footer"))[0].Text;
                lastUpdateInfo = lastUpdateInfo.Split(new string[] { "\r\n", "\r", "\n" }, StringSplitOptions.None)[1];
                return lastUpdateInfo;
            }
            catch (Exception)
            {
                return DateTime.Now.ToString();
            }
        }

        private void LoadAvailableMobalyticsBuilds()
        {
            try
            {
                MobalyticsBuilds.Clear();

                string directory = @".\Builds\Mobalytics";
                if (Directory.Exists(directory))
                {
                    var fileEntries = Directory.EnumerateFiles(directory).Where(tooltip => tooltip.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
                    foreach (string fileName in fileEntries)
                    {
                        string json = File.ReadAllText(fileName);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            MobalyticsBuild? mobalyticsBuild = JsonSerializer.Deserialize<MobalyticsBuild>(json);
                            if (mobalyticsBuild != null)
                            {
                                MobalyticsBuilds.Add(mobalyticsBuild);
                            }
                        }
                    }

                    _eventAggregator.GetEvent<MobalyticsBuildsLoadedEvent>().Publish();
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, MethodBase.GetCurrentMethod()?.Name);
            }
        }

        public void RemoveMobalyticsBuild(string buildId)
        {
            string directory = @".\Builds\Mobalytics";
            File.Delete(@$"{directory}\{buildId}.json");
            LoadAvailableMobalyticsBuilds();
        }

        #endregion
    }
}