// NobleSociety/Addons/IdleNobleNeutralSeatingBehavior.cs
using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.SaveSystem;

namespace NobleSociety.Addons
{
    public sealed class IdleNobleNeutralSeatingBehavior : CampaignBehaviorBase
    {
        // ---- Tunables
        private const float MinStayDays = 5f;             // pin nobles for at least this long once redirected
        private const float RedirectCooldownDays = 15f;   // how often a noble can be redirected
        private const int MaxGuestsPerTown = 8;         // cap per town to avoid clustering
        private const float MaxRedirectDistance = 150f;   // don’t yo-yo across the map

        // Optional: culture “hub” towns to bias (stringIds)
        private static readonly Dictionary<string, string[]> CultureHubs = new Dictionary<string, string[]>
        {
            // {"vlandia", new[]{ "sargot", "jaculan" }},
            // {"aserai",  new[]{ "quyaz",  "sanala"  }},
        };

        // ---- State (primitives only: safe to serialize everywhere)
        private Dictionary<string, string> _seatByHeroId = new Dictionary<string, string>(512);   // heroId -> settlementId
        private Dictionary<string, float> _lockUntil = new Dictionary<string, float>(512);    // heroId -> day
        private Dictionary<string, float> _lastRedirect = new Dictionary<string, float>(512);    // heroId -> day

        public override void RegisterEvents()
        {
            CampaignEvents.DailyTickHeroEvent.AddNonSerializedListener(this, OnDailyTickHero);
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, OnDailyTick);
        }

        public override void SyncData(IDataStore dataStore)
        {
            dataStore.SyncData("NS_SeatByHeroId", ref _seatByHeroId);
            dataStore.SyncData("NS_LockUntil", ref _lockUntil);
            dataStore.SyncData("NS_LastRedirect", ref _lastRedirect);

            if (_seatByHeroId == null) _seatByHeroId = new Dictionary<string, string>(512);
            if (_lockUntil == null) _lockUntil = new Dictionary<string, float>(512);
            if (_lastRedirect == null) _lastRedirect = new Dictionary<string, float>(512);
        }

        private void OnDailyTick()
        {
            // Clean expired locks so towns free up capacity
            float now = (float)CampaignTime.Now.ToDays;
            foreach (var heroId in _lockUntil.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
            {
                _seatByHeroId.Remove(heroId);
                _lockUntil.Remove(heroId);
                _lastRedirect.Remove(heroId);
            }
        }

        private void OnDailyTickHero(Hero hero)
        {
            if (!IsEligible(hero)) return;

            string id = hero.StringId;
            float today = (float)CampaignTime.Now.ToDays;

            // If pinned, enforce lock/cooldown and release if engine moved them
            if (_seatByHeroId.TryGetValue(id, out var seatSettlementId))
            {
                if (hero.PartyBelongedTo != null || hero.CurrentSettlement == null ||
                    hero.CurrentSettlement.StringId != seatSettlementId)
                {
                    Release(id);
                    return;
                }

                if (_lockUntil.TryGetValue(id, out var lockUntil) && today < lockUntil) return;
                if (_lastRedirect.TryGetValue(id, out var last) && (today - last) < RedirectCooldownDays) return;
            }

            var currentTown = hero.CurrentSettlement?.Town;

            // Try to redirect if they sit in their own clan’s town (or if they currently have no town)
            if (currentTown != null && currentTown.OwnerClan == hero.Clan)
            {
                TryRedirect(hero, currentTown);
            }
            else if (currentTown == null && hero.Clan?.Kingdom != null)
            {
                TryRedirect(hero, null);
            }
        }

        private bool IsEligible(Hero h)
        {
            if (h == null || !h.IsAlive || h.IsChild || !h.IsLord) return false;
            if (h.IsPrisoner) return false;
            if (h.PartyBelongedTo != null) return false; // only idle nobles
            if (h.Clan == null || h.Clan.Kingdom == null) return false;
            return true;
        }

        private void TryRedirect(Hero hero, Town current)
        {
            var target = PickNeutralHub(hero, current);
            if (target == null) return;

            // Capacity check at target
            int guestsHere = _seatByHeroId.Values.Count(v => v == target.Settlement.StringId);
            if (guestsHere >= MaxGuestsPerTown) return;

            // Distance budget
            if (current != null)
            {
                float d = current.Settlement.Position2D.Distance(target.Settlement.Position2D);
                if (d > MaxRedirectDistance) return;
            }

            // Move and pin — leave if in a settlement, then enter destination
            if (hero.CurrentSettlement != null)
                LeaveSettlementAction.ApplyForCharacterOnly(hero);

            EnterSettlementAction.ApplyForCharacterOnly(hero, target.Settlement);

            float now = (float)CampaignTime.Now.ToDays;
            string id = hero.StringId;
            _seatByHeroId[id] = target.Settlement.StringId;
            _lockUntil[id] = now + MBRandom.RandomFloatRanged(MinStayDays, MinStayDays + 2f);
            _lastRedirect[id] = now;
        }

        private void Release(string heroId)
        {
            _seatByHeroId.Remove(heroId);
            _lockUntil.Remove(heroId);
            _lastRedirect.Remove(heroId);
        }

        private Town PickNeutralHub(Hero hero, Town current)
        {
            var kingdom = hero.Clan?.Kingdom;
            if (kingdom == null) return null;

            IEnumerable<Town> q = Town.AllTowns.Where(t =>
                t?.Settlement != null &&
                t.MapFaction == kingdom &&
                t.OwnerClan != hero.Clan &&
                !IsUnderSiege(t.Settlement));

            var cultureId = hero.Culture?.StringId ?? "";
            var hubSet = CultureHubs.TryGetValue(cultureId, out var list)
                ? new HashSet<string>(list)
                : new HashSet<string>();

            q = q
                .OrderByDescending(t =>
                    t.Prosperity +
                    (hubSet.Contains(t.Settlement.StringId) ? 1000 : 0))
                .ThenBy(t => current == null ? 0f : current.Settlement.Position2D.Distance(t.Settlement.Position2D));

            return q.FirstOrDefault();
        }

        private static bool IsUnderSiege(Settlement s)
        {
            try { return s?.IsUnderSiege ?? false; } catch { return false; }
        }
    }
}
