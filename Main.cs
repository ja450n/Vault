using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Data;
using System.IO;

using Terraria;
using TShockAPI;
using TShockAPI.DB;

using Hooks;
using Mono.Data.Sqlite;
using MySql.Data.MySqlClient;
using Newtonsoft.Json;

namespace Vault
{
    internal class BossNPC
    {
        public NPC Npc;
        public Dictionary<int, int> damageData = new Dictionary<int,int>();
        public BossNPC(NPC npc)
        {
            this.Npc = npc;
        }
        public void AddDamage(int playerID, int damage)
        {
            if (damageData.ContainsKey(playerID))
                damageData[playerID] += damage;
            else
                damageData.Add(playerID, damage);
        }
        public Dictionary<int, int> GetRecalculatedReward()
        {
            int totalDmg = 0;
            damageData.Values.ForEach(v => totalDmg += v);
            float valueMod = (float)totalDmg / (float)Npc.lifeMax;
            if (valueMod > 1)
                valueMod = 1;
            float newValue = (float)Npc.value * valueMod;
            float valuePerDmg = (float)newValue / (float)totalDmg;
            //Console.WriteLine("Total dmg: {0}, newValue: {1}, val/dmg: {2}", totalDmg, newValue, valuePerDmg);
            Dictionary<int, int> returnDict = new Dictionary<int,int>();
            foreach (KeyValuePair<int,int> kv in damageData)
                returnDict[kv.Key] = (int)(kv.Value * valuePerDmg);
            return returnDict;
        }

    }
    [APIVersion(1, 12)]
    public class Vault : TerrariaPlugin
    {
        public IDbConnection Database;
        public String SavePath = Path.Combine(TShock.SavePath, "Vault/");
        internal PlayerData[] PlayerList = new PlayerData[256];
        internal Config config;
        private readonly Random random = new Random();
        private readonly object _randlocker = new object();
        public delegate void MoneyEvent(TSPlayer sender, int ammount, int newTotal, HandledEventArgs args);
        internal List<BossNPC> BossList = new List<BossNPC>();
        public override string Name
        {
            get { return "Vault"; }
        }
        public override string Author
        {
            get { return "by InanZen"; }
        }
        public override string Description
        {
            get { return ""; }
        }
        public override Version Version
        {
            get { return new Version("0.11"); }
        }

        //-------------------- Static ----------------------------------------------------
        private static Vault CurrentInstance = null;
        public static List<MoneyEvent> MoneyEventHandlers = new List<MoneyEvent>();
        public static int GetBalance(string Name)
        {
            try
            {
                var Reader = CurrentInstance.Database.QueryReader("SELECT money FROM vault_players WHERE username = @0 AND worldID = @1", Name, Main.worldID);          
                if (Reader.Read())
                    return Reader.Get<int>("money");
            }
            catch (Exception ex) { Log.ConsoleError(ex.ToString()); }
            return -1;
        }
        public static bool SetBalance(string Name, int Ammount)
        {
            try
            {
                var player = CurrentInstance.PlayerList.First(p => p.TSPlayer.Name == Name);
                if (player != null)
                    player.Money = Ammount;
                else
                    CurrentInstance.Database.Query("UPDATE vault_players SET money = @0 WHERE username = @1 AND worldID = @2", Ammount, Name, Main.worldID);
                return true;
            }
            catch (Exception ex) { Log.ConsoleError(ex.ToString()); }
            return false;
        }
        public static bool ModifyBalance(string Name, int Ammount)
        {
            try
            {
                var player = CurrentInstance.PlayerList.First(p => p.TSPlayer.Name == Name);
                if (player != null)
                {
                    if (player.ChangeMoney(Ammount))
                        return true;
                }
                else
                {
                    var Reader = CurrentInstance.Database.QueryReader("SELECT money FROM vault_players WHERE username = @0 AND worldID = @1", Name, Main.worldID);
                    if (Reader.Read() && Reader.Get<int>("money") >= Ammount * -1)
                    {
                        int NewAmmount = Reader.Get<int>("money") + Ammount;
                        Reader.Dispose();
                        CurrentInstance.Database.Query("UPDATE vault_players SET money = @0 WHERE username = @1 AND worldID = @2", NewAmmount, Name, Main.worldID);
                        return true;
                    }
                    Reader.Dispose();
                }
            }
            catch (Exception ex) { Log.ConsoleError(ex.ToString()); }
            return false;
        }
        public static Dictionary<int, int> GetPlayerKillCounts(string Name)
        {
            Dictionary<int, int> returnDict = new Dictionary<int, int>();
            if (CurrentInstance.config.LogKillCounts)
            {
                try
                {
                    var reader = CurrentInstance.Database.QueryReader("SELECT killData FROM vault_players WHERE username = @0 AND worldID = @1", Name, Main.worldID);
                    if (reader.Read())
                        returnDict = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<int, int>>(reader.Get<string>("killData"));
                    reader.Dispose();
                }
                catch (Exception ex) { Log.ConsoleError(ex.ToString()); }
            }
            return returnDict;
        }

        //--------------------------------------------------------------------------------
        public Vault(Main game) : base(game)
        {
            Order = 1;
            CurrentInstance = this;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                NetHooks.GetData -= OnGetData;
                NetHooks.SendData -= OnSendData;
                GameHooks.Initialize -= OnInitialize;
                ServerHooks.Leave -= OnLeave;
                ServerHooks.Join -= OnJoin;

                Database.Dispose();
            }
        }
        public override void Initialize()
        {
            NetHooks.GetData += OnGetData;
            NetHooks.SendData += OnSendData;
            GameHooks.Initialize += OnInitialize;            
            ServerHooks.Leave += OnLeave;
            ServerHooks.Join += OnJoin;
        }
        void OnInitialize()
        {
            config = new Config();
            if (!Directory.Exists(SavePath))
                Directory.CreateDirectory(SavePath);
            ReadConfig();
            switch (TShock.Config.StorageType.ToLower())
            {
                case "mysql":
                    string[] host = TShock.Config.MySqlHost.Split(':');
                    Database = new MySqlConnection()
                    {
                        ConnectionString = string.Format("Server={0}; Port={1}; Database={2}; Uid={3}; Pwd={4};",
                            host[0],
                            host.Length == 1 ? "3306" : host[1],
                            TShock.Config.MySqlDbName,
                            TShock.Config.MySqlUsername,
                            TShock.Config.MySqlPassword)
                    };
                    break;
                case "sqlite":
                    string sql = Path.Combine(SavePath, "vault.sqlite");
                    Database = new SqliteConnection(string.Format("uri=file://{0},Version=3", sql));
                    break;
            }
            SqlTableCreator sqlcreator = new SqlTableCreator(Database, Database.GetSqlType() == SqlType.Sqlite ? (IQueryBuilder)new SqliteQueryCreator() : new MysqlQueryCreator());
            sqlcreator.EnsureExists(new SqlTable("vault_players",
                new SqlColumn("username", MySqlDbType.VarChar) { Length = 30 },
                new SqlColumn("money", MySqlDbType.Int32),
                new SqlColumn("worldID", MySqlDbType.Int32),
                new SqlColumn("killData", MySqlDbType.Text)                
                ));


            Commands.ChatCommands.Add(new Command("vault.pay", PayCommand, "pay"));
            Commands.ChatCommands.Add(new Command("vault.balance", BalanceCommand, "balance"));

        }
        public void PayCommand(CommandArgs args)
        {
            int amount;
            if (args.Parameters.Count == 2 && int.TryParse(args.Parameters[1], out amount) && amount > 0)
            {
                var player = PlayerList[args.Player.Index];
                if (player != null)
                {
                    var targetPlayer = GetPlayerByName(args.Parameters[0]);
                    if (targetPlayer == null)
                    {
                        args.Player.SendMessage(String.Format("Player {0} not found", args.Parameters[0]), Color.DarkOrange);
                        return;
                    }
                    if (player.ChangeMoney(-amount) && targetPlayer.ChangeMoney(amount))
                    {
                        targetPlayer.TSPlayer.SendMessage(String.Format("You've received {0} from {1}", PlayerData.MoneyToString(amount), args.Player.Name), Color.DarkGreen);
                        args.Player.SendMessage("Transfer successful", Color.DarkGreen);
                        return;
                    }
                    args.Player.SendMessage("Transfer failed. Check your balance?", Color.DarkOrange);
                    return;
                }
            }
            args.Player.SendMessage("Syntax: /pay \"user name\" ammount", Color.DarkOrange);
        }
        public void BalanceCommand(CommandArgs args)
        {
            var player = PlayerList[args.Player.Index];
            if (player != null)
                args.Player.SendMessage(String.Format("Balance: {0}", PlayerData.MoneyToString(player.Money)), Color.DarkOliveGreen);
        }

        public void OnJoin(int who, HandledEventArgs args)
        {
            PlayerList[who] = new PlayerData(this, TShock.Players[who]);
        }
        public void OnLeave(int who)
        {
            try
            {
                if (PlayerList[who] != null)
                    PlayerList[who].StopUpdating();
                PlayerList[who] = null;
            }
            catch (Exception ex)
            {
                Log.ConsoleError(ex.ToString());
                if (who >= 0)
                    PlayerList[who] = null;
            }
        }
        public void OnGetData(GetDataEventArgs e)
        {
            /*if (e.Handled)
                return;*/
            try
            {
                switch (e.MsgID)
                {
                    case PacketTypes.PlayerUpdate:
                        {
                            byte plyID = e.Msg.readBuffer[e.Index];
                            byte flags = e.Msg.readBuffer[e.Index + 1];
                            var player = PlayerList[plyID];
                            if (player != null && player.LastState != flags)
                            {
                                player.LastState = flags;
                                player.IdleCount = 0;
                            }
                            break;
                        }
                }
            }
            catch (Exception ex) { Log.ConsoleError(ex.ToString()); }
        }
        public void OnSendData(SendDataEventArgs e)
        {
            try
            {
                 switch (e.MsgID)
                {                               
                     case PacketTypes.NpcStrike:
                         {
                             NPC npc = Main.npc[e.number];
                            // Console.WriteLine("(SendData) NpcStrike -> 1:{0} 2:{4} 3:{5} 4:{6} 5:{1} remote:{2} ignore:{3}", e.number, e.number5, e.remoteClient, e.ignoreClient, e.number2, e.number3, e.number4);
                            // Console.WriteLine("NPC: life:{0} reallife:{1}", npc.life);
                             if (npc != null)
                             {
                                 if (npc.boss)
                                 {
                                     if (npc.life <= 0)
                                     {
                                         for (int i = BossList.Count - 1; i >= 0; i--)
                                         {
                                             if (BossList[i].Npc == null)                                             
                                                 BossList.RemoveAt(i);                                             
                                             else if (BossList[i].Npc == npc)
                                             {
                                                 var rewardDict = BossList[i].GetRecalculatedReward();
                                                 foreach (KeyValuePair<int, int> reward in rewardDict)
                                                 {
                                                     if (PlayerList[reward.Key] != null)
                                                         PlayerList[reward.Key].ChangeMoney(reward.Value, config.AnnounceBossGain);
                                                 } 
                                                 BossList.RemoveAt(i);  
                                             }
                                             else if (!BossList[i].Npc.active)
                                                 BossList.RemoveAt(i);                                             
                                         }

                                         if (e.ignoreClient >= 0)
                                         {
                                              var player = PlayerList[e.ignoreClient];
                                              if (player != null)
                                                  player.AddKill(npc.netID);
                                         }
                                     }
                                     else if (e.ignoreClient >= 0)
                                     {
                                         var bossnpc = BossList.Find(n => n.Npc == npc);
                                         if (bossnpc != null)
                                             bossnpc.AddDamage(e.ignoreClient, (int)e.number2);
                                         else
                                         {
                                             BossNPC newBoss = new BossNPC(npc);
                                             newBoss.AddDamage(e.ignoreClient, (int)e.number2);
                                             BossList.Add(newBoss);
                                         }
                                     }
                                 }
                                 else if (npc.life <= 0 && e.ignoreClient >= 0)
                                 {
                                     var player = PlayerList[e.ignoreClient];
                                     if (player != null)
                                     {
                                         if (npc.value > 0)
                                         {
                                             float Mod = 1;
                                             if (player.TSPlayer.TPlayer.buffType.Contains(13)) // battle potion
                                                 Mod *= config.BattlePotionModifier;
                                             if (config.OptionalMobModifier.ContainsKey(npc.netID))
                                                 Mod *= config.OptionalMobModifier[npc.netID]; // apply custom modifiers                                        

                                             int minVal = (int)((npc.value - (npc.value * 0.1)) * Mod);
                                             int maxVal = (int)((npc.value + (npc.value * 0.1)) * Mod);
                                             int rewardAmt = RandomNumber(minVal, maxVal);
                                             player.ChangeMoney(rewardAmt, config.AnnounceKillGain);
                                         }
                                         player.AddKill(npc.netID);
                                     }
                                 }
                             }

                             break;
                         }
                }
            }            
            catch (Exception ex) { Log.ConsoleError(ex.ToString()); }
        }


        internal class Config
        {
            public int InitialMoney = 0;
            public float BattlePotionModifier = 0.75f;
            public Dictionary<int, float> OptionalMobModifier = new Dictionary<int, float>();
            public bool GiveTimedPay = true;
            public byte PayEveryMinutes = 10;
            public byte MaxIdleTime = 3;
            public int PayAmmount = 5000;
            public bool AnnounceTimedPay = false;
            public bool AnnounceKillGain = false;
            public bool AnnounceBossGain = true;
            public bool LogKillCounts = true;
            
        }
        private void CreateConfig()
        {
            string filepath = Path.Combine(SavePath, "config.json");
            try
            {
                using (var stream = new FileStream(filepath, FileMode.Create, FileAccess.Write, FileShare.Write))
                {
                    using (var sr = new StreamWriter(stream))
                    {
                        config = new Config();
                        var configString = JsonConvert.SerializeObject(config, Formatting.Indented);
                        sr.Write(configString);
                    }
                    stream.Close();
                }
            }
            catch (Exception ex)
            {
                Log.ConsoleError(ex.Message);
                config = new Config();
            }
        }
        private bool ReadConfig()
        {
            string filepath = Path.Combine(SavePath, "config.json");
            try
            {
                if (File.Exists(filepath))
                {
                    using (var stream = new FileStream(filepath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        using (var sr = new StreamReader(stream))
                        {
                            var configString = sr.ReadToEnd();
                            config = JsonConvert.DeserializeObject<Config>(configString);
                        }
                        stream.Close();
                    }
                    return true;
                }
                else
                {
                    Log.ConsoleError("Vault config not found. Creating new one");
                    CreateConfig();
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.ConsoleError(ex.Message);
            }
            return false;
        }
        internal int RandomNumber(int min, int max)
        {
            lock (_randlocker)
            {
                return random.Next(min, max);
            }
        }
        internal PlayerData GetPlayerByName(string name)
        {
            for (int i = 0; i < PlayerList.Length; i++)
                if (PlayerList[i] != null && PlayerList[i].TSPlayer.Name.ToLower() == name.ToLower())
                    return PlayerList[i];
            return null;
        }
    }
}
