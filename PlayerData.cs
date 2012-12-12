using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using TShockAPI;
using TShockAPI.DB;
using Terraria;
using System.Threading;
namespace Vault
{
    internal class PlayerData
    {
        private Vault main;
        public TSPlayer TSPlayer;
        public byte LastState = 0;
        public byte IdleCount = 0;
        private int money;
        public int Money
        {
            get { return money; }
            set
            {
                main.Database.Query("UPDATE vault_players SET money = @0 WHERE username = @1 AND worldID = @2", value, TSPlayer.Name, Main.worldID);
                money = value;
            }
        }
        public bool ChangeMoney(int amount, bool announce = false)
        {
            HandledEventArgs args = new HandledEventArgs();
            if (this.money >= amount * -1)
            {                
                if (!Vault.InvokeEvent(this.TSPlayer, amount, this.money + amount, args))
                {
                    this.Money += amount;
                    if (announce)
                        TSPlayer.SendMessage(String.Format("You've {1} {0}", Vault.MoneyToString(amount), amount >= 0 ? "gained" : "lost"), Color.DarkOrange);
                    return true;
                }
            }
            return false;
        }
        public Dictionary<int, int> GetKillCounts()
        {
            Dictionary<int, int> returnDict = new Dictionary<int, int>();
            var reader = main.Database.QueryReader("SELECT killData FROM vault_players WHERE username = @0 AND worldID = @1", TSPlayer.Name, Main.worldID);
            if (reader.Read())
                returnDict = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<int, int>>(reader.Get<string>("killData"));
            reader.Dispose();
            return returnDict;
        }
        public void AddKill(int mobID)
        {
            if (main.config.LogKillCounts)
            {
                var killDict = GetKillCounts();
                if (killDict.ContainsKey(mobID))
                    killDict[mobID] += 1;
                else
                    killDict.Add(mobID, 1);
                main.Database.Query("UPDATE vault_players SET killData = @0 WHERE username = @1 AND worldID = @2", Newtonsoft.Json.JsonConvert.SerializeObject(killDict), TSPlayer.Name, Main.worldID);
            }
        }
        public PlayerData(Vault instance, TSPlayer player)
        {
            main = instance;
            TSPlayer = player;
            UpdatePlayerData();
            if (main.config.GiveTimedPay)
                StartUpdating();
        }
        public void UpdatePlayerData()
        {
            QueryResult result = main.Database.QueryReader("SELECT * FROM vault_players WHERE username = @0 AND worldID = @1", TSPlayer.Name, Main.worldID);
            if (result.Read())
            {
                this.money = result.Get<int>("money");
            }
            else
            {
                this.money = main.config.InitialMoney;
                main.Database.Query("INSERT INTO vault_players(username, money, worldID, killData) VALUES(@0,@1,@2,@3)", TSPlayer.Name, this.money, Main.worldID, Newtonsoft.Json.JsonConvert.SerializeObject(new Dictionary<int,int>()));
            }
            result.Dispose();
        }


        public Thread UpdateThread = null;
        public void StartUpdating()
        {
            try
            {
                if (this.UpdateThread == null || !this.UpdateThread.IsAlive)
                {
                    var updater = new Updater(main, TSPlayer.Index);
                    this.UpdateThread = new Thread(updater.PayTimer);
                    this.UpdateThread.Start();
                }
            }
            catch (Exception ex) { Log.ConsoleError(ex.ToString()); }
        }
        public void StopUpdating()
        {
            try
            {
                if (this.UpdateThread != null)
                    this.UpdateThread.Abort();
            }
            catch (Exception ex) { Log.ConsoleError(ex.ToString()); }
        }

        // -------------------------------- UPDATER ----------------------------------------------------------
        private class Updater
        {
            int who;
            Vault main;
            byte TimerCount;
            public Updater(Vault instance, int who)
            {
                this.main = instance;
                this.who = who;
                this.TimerCount = 0;
            }
            public void PayTimer()
            {
                while (Thread.CurrentThread.IsAlive && main != null)
                {
                    var player = main.PlayerList[who];
                    if (player != null)
                    {
                        try
                        {
                            if (player.IdleCount < main.config.MaxIdleTime)
                            {
                                player.IdleCount++;
                                if (this.TimerCount == main.config.PayEveryMinutes)
                                    player.ChangeMoney(main.config.Payamount, main.config.AnnounceTimedPay);
                                this.TimerCount++;
                                if (this.TimerCount > main.config.PayEveryMinutes)
                                    this.TimerCount = 1;
                            }
                        }
                        catch (Exception ex) { Log.ConsoleError(ex.ToString()); }
                        Thread.Sleep(60000);
                    }
                    else
                        Thread.CurrentThread.Abort();
                }
            }
        }

    }
}
