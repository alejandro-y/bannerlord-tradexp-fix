#define CONSIDER_OLD_KEY_NAMES

using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections;

using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.SandBox.CampaignBehaviors;
using TaleWorlds.Library;

namespace TradeXPFixModule {
    internal static class IDictionaryExtension {
        public static void Deconstruct(this DictionaryEntry de, out ItemRosterElement itemRosterElement, out object itemTradeData) {
            itemRosterElement = (ItemRosterElement)de.Key;
            itemTradeData = de.Value;
        }
    }

    internal static class ItemRosterElementExtension {
        // note: see Modules/SandBoxCore/ModuleData/spitems.xml
        //      Type="Goods" / is_mountable="true" / IsFood="true" / Type="Animal"
        public static bool IsProfitable(this ItemRosterElement itemRosterElement) =>
            itemRosterElement.EquipmentElement.Item.IsTradeGood
            || itemRosterElement.EquipmentElement.Item.IsMountable
            || itemRosterElement.EquipmentElement.Item.IsFood // currently (1.1.1) covered by IsTradeGood anyway
            || itemRosterElement.EquipmentElement.Item.IsAnimal;
    }

    internal class TradeXPFixBehavior: CampaignBehaviorBase {
        private bool tradeInited = false;
        private IDictionary tradeBhvItemsTradeData;
        private static readonly Type typeofItemTradeData = typeof(TradeSkillCampaingBehavior).FindNestedType("ItemTradeData");

        public override void RegisterEvents() {
            // NOP
        }

        public override void SyncData(IDataStore dataStore) {
            InitItemsTradeDataRef();
            // Using three lists on purpose as this seems to be the only way to keep something in a save without breaking it.
            // List<Tuple<float, int>> (or ValueTuple) (or "int, float") doesn't save.
            // Queue<object> (or List) doesn't save.
            // I reckon byte[] of serialized List<ValueTuple<...>> would save, but the code would be even worse because ItemRosterElement
            // is not [Serializable].
            var itemRosterElements = new List<ItemRosterElement>(tradeBhvItemsTradeData.Count);
            var avgPrices = new List<float>(tradeBhvItemsTradeData.Count);
            var numsItemsPurchased = new List<int>(tradeBhvItemsTradeData.Count);
            if (dataStore.IsSaving) {
                foreach (DictionaryEntry de in tradeBhvItemsTradeData) {
                    var (itemRosterElement, itemTradeData) = de;
                    // It was reported that (1) ItemModifier and (2) excess amount of items can corrupt saves.
                    // I couldn't reproduce either case with this code (perhaps because the other mod reuses the same behavior instance?).
                    // (2) is impossible as for perf. reasons I'm only saving "profitable" items.
                    // And it does not harm to merge a fix for (1). Credit goes to Maegfaer@Nexusmods.
                    if (itemRosterElement.EquipmentElement.ItemModifier is null && itemRosterElement.IsProfitable()) {
                        // Partial fix for stale # of consumable items, credits: Maegfaer@Nexusmods.
                        // TODO Possible options for a more comprehensive fix: OnPartyConsumedFoodEvent, PlayerInventoryExchangeEvent,
                        // DailyTick and friends.
                        var numItemsPurchased = Math.Min(
                            itemTradeData.GetFieldValue<int>("NumItemsPurchased"),
                            PartyBase.MainParty.ItemRoster.GetItemNumber(itemRosterElement.EquipmentElement.Item));
                        if (numItemsPurchased > 0) {
                            itemRosterElements.Add(itemRosterElement);
                            avgPrices.Add(itemTradeData.GetFieldValue<float>("AveragePrice"));
                            numsItemsPurchased.Add(numItemsPurchased);
                        }
                    }
                }
            }
            dataStore.SyncData("fixTD_itemRosterElements", ref itemRosterElements);
            dataStore.SyncData("fixTD_avgPrices", ref avgPrices);
            dataStore.SyncData("fixTD_numsItemsPurchased", ref numsItemsPurchased);
#if CONSIDER_OLD_KEY_NAMES
            if (dataStore.IsLoading && itemRosterElements.IsEmpty()) {
                dataStore.SyncData("fixTD_itemRosterElementList", ref itemRosterElements);
                dataStore.SyncData("fixTD_avgPriceList", ref avgPrices);
                dataStore.SyncData("fixTD_numItemsPurchasedList", ref numsItemsPurchased);
            }
#endif
            if (dataStore.IsLoading) {
                for (int i = 0; i < itemRosterElements.Count; i++) {
                    var itemTradeData = typeofItemTradeData.New(avgPrices[i], numsItemsPurchased[i]);
                    tradeBhvItemsTradeData[itemRosterElements[i]] = itemTradeData;
                }
            }
        }

        private void InitItemsTradeDataRef() {
            if (!tradeInited) {
                // IDictionary because we need to iterate ItemsTradeData but:
                //  - can't cast to Dictionary<ItemRosterElement, ItemTradeData> because ItemTradeData is private;
                //  - can't cast to Dictionary<ItemRosterElement, object> because it is Dictionary<ItemRosterElement, ItemTradeData>.
                tradeBhvItemsTradeData = Campaign.Current.GetCampaignBehavior<TradeSkillCampaingBehavior>()
                                                         .GetFieldValue<object>("tradeHistory")
                                                         .GetFieldValue<IDictionary>("ItemsTradeData");
                tradeInited = true;
            }
        }

        // applicable as long as TradeSkillCampaingBehavior.ItemTradeData is not Saveable and our API assumptions hold.
        internal static bool IsApplicable() {
            var typeofItemTradeData = typeof(TradeSkillCampaingBehavior).FindNestedType("ItemTradeData");

            if (typeofItemTradeData is null) {
                return false;
            }
            if (typeofItemTradeData.CustomAttributes.Any(a => a.AttributeType == typeof(TaleWorlds.SaveSystem.SaveableStructAttribute)
                                                              || a.AttributeType == typeof(TaleWorlds.SaveSystem.SaveableClassAttribute))) {
                return false;
            }

            var typeofItemsTradeData = typeof(TradeSkillCampaingBehavior).FindField("tradeHistory")?.FieldType
                                                                         .FindField("ItemsTradeData")?.FieldType;
            if (typeofItemsTradeData?.IsGenericType != true || typeofItemsTradeData.GetGenericTypeDefinition() != typeof(Dictionary<,>)) {
                return false;
            }
            if (typeofItemsTradeData.GenericTypeArguments[0] != typeof(ItemRosterElement)
                || typeofItemsTradeData.GenericTypeArguments[1] != typeofItemTradeData) {
                return false;
            }

            if (!typeofItemTradeData.GetAllFields().Any(f => f.FieldType == typeof(float) && f.Name == "AveragePrice")) {
                return false;
            }
            if (!typeofItemTradeData.GetAllFields().Any(f => f.FieldType == typeof(int) && f.Name == "NumItemsPurchased")) {
                return false;
            }

            var itemTradeDataCtor = typeofItemTradeData.GetConstructors().FirstOrDefault();
            if (itemTradeDataCtor?.GetParameters().Length != 2) {
                return false;
            }
            if (!itemTradeDataCtor.GetParameters().Any(p => p.Position == 0 && p.ParameterType == typeof(float) && p.Name == "averagePrice")) {
                return false;
            }
            if (!itemTradeDataCtor.GetParameters().Any(p => p.Position == 1 && p.ParameterType == typeof(int) && p.Name == "numItemsPurchased")) {
                return false;
            }

            return true;
        }
    }

    public class TradeXPFixSubModule: MBSubModuleBase {
        protected override void OnGameStart(Game game, IGameStarter gameStarter) {
            if (game.GameType is Campaign) {
                if (TradeXPFixBehavior.IsApplicable()) {
                    CampaignGameStarter campaignStarter = (CampaignGameStarter)gameStarter;
                    campaignStarter.AddBehavior(new TradeXPFixBehavior());
                } else {
                    InformationManager.DisplayMessage(new InformationMessage("TradeXPFixBehavior.IsApplicable() = false", Colors.Red));
                }
            }
        }
    }
}
