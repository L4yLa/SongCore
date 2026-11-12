using System.IO;
using BGLib.Polyglot;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using SongCore.Utilities;
using System.Linq;
using System.Reflection;
using UnityEngine;
using static BeatSaberMarkupLanguage.Components.CustomListTableData;
using HMUI;
using SongCore.Data;
using Tweening;
using UnityEngine.UI;
using Zenject;

namespace SongCore.UI
{
    public class RequirementsUI : NotifiableBase, IInitializable
    {
        private readonly StandardLevelDetailViewController _standardLevelDetailViewController;
        private readonly CustomLevelLoader _customLevelLoader;
        private readonly EnvironmentsListModel _environmentsListModel;
        private readonly TimeTweeningManager _tweeningManager;
        private readonly BSMLParser _bsmlParser;
        private readonly PluginConfig _config;
        private readonly ColorsUI _colorsUI;

        private RequirementsUI(StandardLevelDetailViewController standardLevelDetailViewController, CustomLevelLoader customLevelLoader, EnvironmentsListModel environmentsListModel, TimeTweeningManager tweeningManager, BSMLParser bsmlParser, PluginConfig config, ColorsUI colorsUI)
        {
            _standardLevelDetailViewController = standardLevelDetailViewController;
            _customLevelLoader = customLevelLoader;
            _environmentsListModel = environmentsListModel;
            _tweeningManager = tweeningManager;
            _bsmlParser = bsmlParser;
            _config = config;
            _colorsUI = colorsUI;
            instance = this;
        }

        public static RequirementsUI instance { get; set; } = null!;

        private const string BUTTON_BSML = "<bg id='root'><action-button id='info-button' text='?' active='~button-glow' interactable='~button-interactable' anchor-pos-x='31' anchor-pos-y='0' pref-width='12' pref-height='9' on-click='button-click'/></bg>";
        private ImageView buttonBG = null!;
        private Color originalColor0;
        private Color originalColor1;

        internal Sprite? HaveReqIcon;
        internal Sprite? MissingReqIcon;
        internal Sprite? HaveSuggestionIcon;
        internal Sprite? MissingSuggestionIcon;
        internal Sprite? WarningIcon;
        internal Sprite? InfoIcon;
        internal Sprite? ColorsIcon;
        internal Sprite? OneSaberIcon;
        internal Sprite? EnvironmentIcon;
        internal Sprite? StandardIcon;

        //Currently selected song data
        public BeatmapLevel? beatmapLevel;
        public BeatmapKey? beatmapKey;
        public SongData? songData;
        public SongData.DifficultyData? diffData;
        public bool wipFolder;
        internal bool over100Percent;

        // Shown instead of the play button while the selected difficulty cannot be played. Created on first use.
        private GameObject? _checkButton;
        private static readonly Color CheckButtonColor = new Color(1f, 0.82f, 0.1f);

        [UIComponent("list")]
        public CustomListTableData customListTableData = null!;

        private bool _buttonGlow = false;

        [UIValue("button-glow")]
        public bool ButtonGlowColor
        {
            get => _buttonGlow;
            set
            {
                _buttonGlow = value;
                NotifyPropertyChanged();
            }
        }

        private bool buttonInteractable = false;

        [UIValue("button-interactable")]
        public bool ButtonInteractable
        {
            get => buttonInteractable;
            set
            {
                buttonInteractable = value;
                NotifyPropertyChanged();
            }
        }

        [UIComponent("modal")]
        private ModalView modal = null!;

        private Vector3 modalPosition;

        [UIComponent("info-button")]
        private Transform infoButtonTransform = null!;

        [UIComponent("root")]
        protected readonly RectTransform _root = null!;

        public void Initialize()
        {
            GetIcons();
            _bsmlParser.Parse(BUTTON_BSML, _standardLevelDetailViewController._standardLevelDetailView.gameObject, this);

            infoButtonTransform.localScale *= 0.7f; //no scale property in bsml as of now so manually scaling it
            ((RectTransform)_standardLevelDetailViewController._standardLevelDetailView._favoriteToggle.transform).anchoredPosition = new Vector2(3, -2);
            buttonBG = infoButtonTransform.Find("BG").GetComponent<ImageView>();
            originalColor0 = buttonBG.color0;
            originalColor1 = buttonBG.color1;
        }

        private void GetIcons()
        {
            if (!MissingReqIcon)
            {
                MissingReqIcon = Utils.LoadSpriteFromResources("SongCore.Icons.RedX.png")!;
            }

            if (!HaveReqIcon)
            {
                HaveReqIcon = Utils.LoadSpriteFromResources("SongCore.Icons.GreenCheck.png")!;
            }

            if (!HaveSuggestionIcon)
            {
                HaveSuggestionIcon = Utils.LoadSpriteFromResources("SongCore.Icons.YellowCheck.png")!;
            }

            if (!MissingSuggestionIcon)
            {
                MissingSuggestionIcon = Utils.LoadSpriteFromResources("SongCore.Icons.YellowX.png")!;
            }

            if (!WarningIcon)
            {
                WarningIcon = Utils.LoadSpriteFromResources("SongCore.Icons.Warning.png")!;
            }

            if (!InfoIcon)
            {
                InfoIcon = Utils.LoadSpriteFromResources("SongCore.Icons.Info.png")!;
            }

            if (!ColorsIcon)
            {
                ColorsIcon = Utils.LoadSpriteFromResources("SongCore.Icons.Colors.png")!;
            }

            if (!EnvironmentIcon)
            {
                EnvironmentIcon = Utils.LoadSpriteFromResources("SongCore.Icons.Environment.png")!;
            }

            if (!OneSaberIcon)
            {
                OneSaberIcon = Utils.LoadSpriteFromResources("SongCore.Icons.OneSaber.png")!;
            }

            if (!StandardIcon)
            {
                StandardIcon = Utils.LoadSpriteFromResources("SongCore.Icons.Standard.png")!;
            }
        }

        [UIAction("button-click")]
        internal void ShowRequirements()
        {
            if (modal == null)
            {
                _bsmlParser.Parse(BeatSaberMarkupLanguage.Utilities.GetResourceContent(Assembly.GetExecutingAssembly(), "SongCore.UI.requirements.bsml"), _root.gameObject, this);
                modalPosition = modal!.transform.localPosition;
            }
            modal.transform.localPosition = modalPosition;
            modal.Show(true);
            customListTableData.Data.Clear();

            //Problems in the info file, listed first since they are why the map cannot be played
            if (songData != null)
            {
                foreach (var error in songData._validationErrors)
                {
                    customListTableData.Data.Add(new CustomCellInfo($"<size=70%>{error}", "Invalid Map Data", MissingReqIcon));
                }

                if (diffData != null)
                {
                    foreach (var error in diffData._validationErrors)
                    {
                        customListTableData.Data.Add(new CustomCellInfo($"<size=70%>{error}", "Invalid Difficulty Data", MissingReqIcon));
                    }
                }

                foreach (var warning in songData._validationWarnings)
                {
                    customListTableData.Data.Add(new CustomCellInfo($"<size=70%>{warning}", "Map Data Warning", WarningIcon));
                }

                if (diffData != null)
                {
                    foreach (var warning in diffData._validationWarnings)
                    {
                        customListTableData.Data.Add(new CustomCellInfo($"<size=70%>{warning}", "Difficulty Data Warning", WarningIcon));
                    }
                }
            }

            //The game could not resolve the characteristic, so the difficulty cannot be played
            if (beatmapKey is { } characteristicKey && characteristicKey.beatmapCharacteristic.serializedName == "MissingCharacteristic")
            {
                customListTableData.Data.Add(new CustomCellInfo("<size=70%>The characteristic of this difficulty is not available. A mod that provides it may be missing.", "Unknown Characteristic", MissingReqIcon));
            }

            //Requirements
            if (diffData != null)
            {
                if (diffData.additionalDifficultyData._requirements.Any())
                {
                    foreach (var req in diffData.additionalDifficultyData._requirements)
                    {
                        customListTableData.Data.Add(!Collections.capabilities.Contains(req)
                            ? new CustomCellInfo($"<size=75%>{req}", "Missing Requirement", MissingReqIcon)
                            : new CustomCellInfo($"<size=75%>{req}", "Requirement", HaveReqIcon));
                    }
                }
            }

            //Blocked because scores can go over 100% on some leaderboards
            if (over100Percent)
            {
                customListTableData.Data.Add(new CustomCellInfo("<size=70%>A chain head in this difficulty is also part of an arc. The game scores it out of 115 instead of 85, so leaderboards such as ScoreSaber may see scores over 100%. Practice is still available, as it does not submit scores. This can be turned off in the SongCore settings.", "May Score Over 100%", MissingReqIcon));
            }

            //Contributors
            if (songData != null && songData.contributors.Length > 0)
            {
                foreach (var author in songData.contributors)
                {
                    if (author.icon == null)
                    {
                        if (author._iconPath is { } iconPath && !string.IsNullOrWhiteSpace(iconPath)
                            && beatmapLevel != null && _customLevelLoader._loadedBeatmapSaveData.TryGetValue(beatmapLevel.levelID, out var loadedSaveData))
                        {
                            author.icon = Utils.LoadSpriteFromFile(Path.Combine(loadedSaveData.customLevelFolderInfo.folderPath, iconPath));
                            customListTableData.Data.Add(new CustomCellInfo(author._name, author._role, author.icon != null ? author.icon : InfoIcon));
                        }
                        else
                        {
                            customListTableData.Data.Add(new CustomCellInfo(author._name, author._role, InfoIcon));
                        }
                    }
                    else
                    {
                        customListTableData.Data.Add(new CustomCellInfo(author._name, author._role, author.icon));
                    }
                }
            }

            //WIP Check
            if (wipFolder)
            {
                customListTableData.Data.Add(new CustomCellInfo("<size=70%>WIP Song. Please Play in Practice Mode", "Warning", WarningIcon));
            }

            //Additional Diff Info
            if (diffData != null)
            {
                if (Utils.DiffHasColors(diffData))
                {
                    customListTableData.Data.Add(new CustomCellInfo($"<size=75%>Custom Colors Available", $"Click here to preview & enable or disable it.", ColorsIcon));
                }
                string? environmentName = null;

                if (diffData._environmentNameIdx != null && songData != null && beatmapLevel != null && beatmapKey is { } environmentKey)
                {
                    var environmentInfoName = songData._environmentNames.ElementAtOrDefault(diffData._environmentNameIdx.Value);
                    if (environmentInfoName != null)
                    {
                        if (environmentInfoName != beatmapLevel.GetEnvironmentName(environmentKey.beatmapCharacteristic, environmentKey.difficulty))
                        {
                            environmentName = _environmentsListModel.GetEnvironmentInfoBySerializedNameSafe(environmentInfoName).environmentName;
                        }
                    }
                }

                if (diffData.additionalDifficultyData._warnings.Length > 0)
                {
                    foreach (var req in diffData.additionalDifficultyData._warnings)
                    {
                        customListTableData.Data.Add(new CustomCellInfo($"<size=75%>{req}", "Warning", WarningIcon));
                    }
                }

                if (diffData.additionalDifficultyData._information.Length > 0)
                {
                    foreach (var req in diffData.additionalDifficultyData._information)
                    {
                        customListTableData.Data.Add(new CustomCellInfo($"<size=75%>{req}", "Info", InfoIcon));
                    }
                }

                if (diffData.additionalDifficultyData._suggestions.Length > 0)
                {
                    foreach (var req in diffData.additionalDifficultyData._suggestions)
                    {
                        customListTableData.Data.Add(!Collections.capabilities.Contains(req)
                            ? new CustomCellInfo($"<size=75%>{req}", "Missing Suggestion", MissingSuggestionIcon)
                            : new CustomCellInfo($"<size=75%>{req}", "Suggestion", HaveSuggestionIcon));
                    }
                }

                if (diffData._oneSaber != null)
                {
                    var enabledText = _config.DisableOneSaberOverride ? "[<color=#ff5072>Disabled</color>]" : "[<color=#89ff89>Enabled</color>]";
                    var enabledSubtext = _config.DisableOneSaberOverride ? "enable" : "disable";
                    var saberCountText = diffData._oneSaber.Value ? "Forced One Saber" : "Forced Standard";
                    customListTableData.Data.Add(new CustomCellInfo($"<size=75%>{saberCountText} {enabledText}", $"Map changes saber count, click here to {enabledSubtext}.", diffData._oneSaber.Value ? OneSaberIcon : StandardIcon));
                }

                if (customListTableData.Data.Count > 0)
                {
                    if (environmentName == null && beatmapLevel != null && beatmapKey is { } key)
                        environmentName = beatmapLevel.GetEnvironmentName(key.beatmapCharacteristic, key.difficulty);
                    customListTableData.Data.Add(new CustomCellInfo("<size=75%>Environment Info", $"This Map uses the Environment: {environmentName}", EnvironmentIcon));

                }
            }

            customListTableData.TableView.ReloadData();
            customListTableData.TableView.ScrollToCellWithIdx(0, TableView.ScrollPositionType.Beginning, false);
        }

        [UIAction("list-select")]
        private void Select(TableView _, int index)
        {
            customListTableData.TableView.ClearSelection();
            if (diffData != null)
            {
                var iconSelected = customListTableData.Data[index].Icon;
                if (iconSelected == ColorsIcon)
                {
                    modal.Hide(false, () => _colorsUI.ShowColors(diffData));
                }
                else if (iconSelected == StandardIcon || iconSelected == OneSaberIcon)
                {
                    _config.DisableOneSaberOverride = !_config.DisableOneSaberOverride;
                    modal.Hide(true);
                }
            }
        }

        /// <summary>
        /// Replaces the play button with a yellow "Check" button that opens the same list as the "?" button.
        /// A disabled play button alone is easy to overlook, so the reason is one click away instead.
        /// </summary>
        internal void SetPlayBlocked(bool blocked)
        {
            var actionButton = _standardLevelDetailViewController._standardLevelDetailView.actionButton;
            if (blocked && _checkButton == null)
            {
                _checkButton = CreateCheckButton(actionButton);
            }

            if (_checkButton != null)
            {
                _checkButton.SetActive(blocked);
            }

            actionButton.gameObject.SetActive(!blocked);
            // Kept in sync as well, since other mods may check whether the play button can be pressed.
            actionButton.interactable = !blocked;
        }

        private GameObject CreateCheckButton(Button actionButton)
        {
            // A copy of the play button keeps its size and its place in the button row.
            var checkButton = Object.Instantiate(actionButton.gameObject, actionButton.transform.parent, false);
            checkButton.name = "SongCoreCheckButton";
            checkButton.transform.SetSiblingIndex(actionButton.transform.GetSiblingIndex() + 1);

            // The localization component would put the play text back, and the state animations would
            // replace the color whenever the button is hovered.
            foreach (var localizedText in checkButton.GetComponentsInChildren<LocalizedTextMeshProUGUI>(true))
            {
                Object.DestroyImmediate(localizedText);
            }

            foreach (var animations in checkButton.GetComponentsInChildren<ButtonStaticAnimations>(true))
            {
                Object.DestroyImmediate(animations);
            }

            var text = checkButton.GetComponentInChildren<TMPro.TMP_Text>(true);
            if (text != null)
            {
                text.text = "Check";
                text.color = Color.black;
            }

            var background = checkButton.transform.Find("BG")?.GetComponent<ImageView>();
            if (background != null)
            {
                background.color = CheckButtonColor;
                background.color0 = Color.white;
                background.color1 = Color.white;
                background.SetAllDirty();
            }

            // Replaced rather than cleared, so that listeners copied from the play button cannot start the level.
            var button = checkButton.GetComponent<Button>();
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(ShowRequirements);
            button.interactable = true;

            return checkButton;
        }

        internal void SetRainbowColors(bool shouldSet, bool firstPulse = true)
        {
            _tweeningManager.KillAllTweens(buttonBG);
            if (shouldSet)
            {
                var tween = new FloatTween(firstPulse ? 0 : 1, firstPulse ? 1 : 0, val =>
                {
                    buttonBG.color0 = new Color(1 - val, val, 0);
                    buttonBG.color1 = new Color(0, 1 - val, val);
                    buttonBG.SetAllDirty();
                }, 5f, EaseType.InOutSine);
                _tweeningManager.AddTween(tween, buttonBG);
                tween.onCompleted = delegate ()
                { SetRainbowColors(true, !firstPulse); };
            }
            else
            {
                buttonBG.color0 = originalColor0;
                buttonBG.color1 = originalColor1;
            }
        }
    }
}
