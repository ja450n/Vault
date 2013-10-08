using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Data;
using System.IO;

using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.DB;
using TShockAPI.Hooks;

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
            float Mod = 1;
            if (Vault.config.OptionalMobModifier.ContainsKey(Npc.netID))
                Mod *= Vault.config.OptionalMobModifier[Npc.netID]; // apply custom modifiers      
            Dictionary<int, int> returnDict = new Dictionary<int,int>();
            foreach (KeyValuePair<int,int> kv in damageData)
                returnDict[kv.Key] = (int)(kv.Value * valuePerDmg * Mod);
            return returnDict;
        }

    }
    [ApiVersion(1, 14)]
    public class Vault : TerrariaPlugin
    {
        public IDbConnection Database;
        public String SavePath = Path.Combine(TShock.SavePath, "Vault/");
        internal PlayerData[] PlayerList = new PlayerData[256];
        internal static Config config;
        private readonly Random random = new Random();
        private readonly object _randlocker = new object();
        internal List<BossNPC> BossList = new List<BossNPC>();
        public override string Name
        {
            get { return "Vault"; }
        }
        public override string Author
        {
            get { return "ja450n - original by InanZen"; }
        }
        public override string Description
        {
            get { return ""; }
        }
        public override Version Version
        {
            get { return new Version("0.17"); }
        }
        public Vault(Main game) : base(game)
        {
            Order = 1;
            CurrentInstance = this;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // NetHooks.GetData -= OnGetData;
                // NetHooks.SendData -= OnSendData;
                // GameHooks.Initialize -= OnInitialize;
                // ServerHooks.Leave -= OnLeave;
                // ServerHooks.Join -= OnJoin;

                ServerApi.Hooks.NetGetData.Deregister(this, OnGetData);
                ServerApi.Hooks.NetSendData.Deregister(this, OnSendData);
                ServerApi.Hooks.GameInitialize.Deregister(this, (args) => { OnInitialize(); });
                ServerApi.Hooks.ServerLeave.Deregister(this, OnLeave);
                ServerApi.Hooks.ServerJoin.Deregister(this, OnJoin);
                
                Database.Dispose();
            }
        }
        public override void Initialize()
        {   
            // NetHooks.GetData += OnGetData;
            // NetHooks.SendData += OnSendData;
            // GameHooks.Initialize += OnInitialize;            
            // ServerHooks.Leave += OnLeave;
            // ServerHooks.Join += OnJoin;

            ServerApi.Hooks.NetGetData.Register(this, OnGetData);
            ServerApi.Hooks.NetSendData.Register(this, OnSendData);
            ServerApi.Hooks.GameInitialize.Register(this, (args) => { OnInitialize(); });
            ServerApi.Hooks.ServerLeave.Register(this, OnLeave);
            ServerApi.Hooks.ServerJoin.Register(this, OnJoin);


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
                new SqlColumn("killData", MySqlDbType.Text),
                new SqlColumn("tempMin", MySqlDbType.Int32),
                new SqlColumn("totalOnline", MySqlDbType.Int32),
                new SqlColumn("lastSeen", MySqlDbType.Text)
                ));


            Commands.ChatCommands.Add(new Command("vault.pay", PayCommand, "pay"));
            Commands.ChatCommands.Add(new Command("vault.balance", BalanceCommand, "balance"));
            Commands.ChatCommands.Add(new Command("vault.modify", ModifyCommand, "modbal", "modifybalance"));
            Commands.ChatCommands.Add(new Command("vault.seen", SeenCommand, "seen"));

        }
        public void SeenCommand(CommandArgs args)
        {
            if (args.Parameters.Count == 1)
            {
                var reader = Database.QueryReader("SELECT lastSeen FROM vault_players WHERE username = @0 AND worldID = @1", args.Parameters[0], Main.worldID);
                bool found = false;
                if (reader.Read())
                {
                    args.Player.SendMessage(String.Format("{0} has last been seen at {1}", args.Parameters[0], JsonConvert.DeserializeObject<DateTime>(reader.Get<string>("lastSeen"))), Color.DarkGreen);
                    found = true;
                }
                reader.Dispose();
                if (!found)
                    args.Player.SendMessage(String.Format("Haven't seen {0}", args.Parameters[0]), Color.DarkOrange);
                return;
            }
            args.Player.SendMessage("Syntax: /seen \"player name\"", Color.DarkOrange);
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
                    if (player.ChangeMoney(-amount, MoneyEventFlags.PayCommand) && targetPlayer.ChangeMoney(amount, MoneyEventFlags.PayCommand))
                    {
                        targetPlayer.TSPlayer.SendMessage(String.Format("You've received {0} from {1}", MoneyToString(amount), args.Player.Name), Color.DarkGreen);
                        args.Player.SendMessage("Transfer successful", Color.DarkGreen);
                        return;
                    }
                    args.Player.SendMessage("Transfer failed. Check your balance?", Color.DarkOrange);
                    return;
                }
            }
            args.Player.SendMessage("Syntax: /pay \"user name\" amount", Color.DarkOrange);
        }
        public void ModifyCommand(CommandArgs args)
        {
            int amount;
            if (args.Parameters.Count == 2 && int.TryParse(args.Parameters[1], out amount))
            {
                if (ModifyBalance(args.Parameters[0], amount))
                    args.Player.SendMessage(String.Format("Player {0}'s balance modified", args.Parameters[0]), Color.DarkGreen);
                else
                    args.Player.SendMessage(String.Format("Can't modify player {0}'s balance. No such player found or balance below zero.", args.Parameters[0]), Color.DarkOrange);
                return;
            }
            args.Player.SendMessage("Syntax: /modbal \"user name\" amount", Color.DarkOrange);
        }
        public void BalanceCommand(CommandArgs args)
        {
            var player = PlayerList[args.Player.Index];
            if (player != null)
            {
                if (args.Parameters.Count > 0 && args.Player.Group.HasPermission("vault.balance.others"))
                {
                    int b = GetBalance(args.Parameters[0]);
                    if (b == -1)
                        args.Player.SendMessage(String.Format("No records for player {0} found", args.Parameters[0]), Color.DarkOrange);
                    else
                        args.Player.SendMessage(String.Format("{0}'s balance: {1}", args.Parameters[0], MoneyToString(b)), Color.DarkOliveGreen);
                    return;
                }
                args.Player.SendMessage(String.Format("Balance: {0}", MoneyToString(player.Money)), Color.DarkOliveGreen);
            }
        }

        public void OnJoin(JoinEventArgs e)
        {
            PlayerList[e.Who] = new PlayerData(this, TShock.Players[e.Who]);
        }
        public void OnLeave(LeaveEventArgs e)
        {
            try
            {
                if (PlayerList[e.Who] != null)
                    PlayerList[e.Who].StopUpdating();
                PlayerList[e.Who] = null;
            }
            catch (Exception ex)
            {
                Log.ConsoleError(ex.ToString());
                if (e.Who >= 0)
                    PlayerList[e.Who] = null;
            }
        }
        public void OnGetData(GetDataEventArgs e)
        {
            try
            {
                if (e.MsgID == PacketTypes.PlayerUpdate)
                {
                    byte plyID = e.Msg.readBuffer[e.Index];
                    byte flags = e.Msg.readBuffer[e.Index + 1];
                    var player = PlayerList[plyID];
                    if (player != null && player.LastState != flags)
                    {
                        player.LastState = flags;
                        player.IdleCount = 0;
                    }
                }            
            }
            catch (Exception ex) { Log.ConsoleError(ex.ToString()); }
        }
        public void OnSendData(SendDataEventArgs e)
        {
            if (e.Handled)
                return;
            try
            {
                if (e.MsgId == PacketTypes.NpcStrike)
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
                                                PlayerList[reward.Key].ChangeMoney(reward.Value, MoneyEventFlags.Kill, config.AnnounceBossGain);
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
                                    player.ChangeMoney(rewardAmt, MoneyEventFlags.Kill, config.AnnounceKillGain);
                                }
                                player.AddKill(npc.netID);
                            }
                        }
                    }
                }
                else if (e.MsgId == PacketTypes.PlayerKillMe)
                {
                    //Console.WriteLine("(SendData) PlayerKillMe -> 1:{0} 2:{4} 3:{5} 4:{6} 5:{1} remote:{2} ignore:{3}", e.number, e.number5, e.remoteClient, e.ignoreClient, e.number2, e.number3, e.number4);
                    // 1-playerID, 2-direction, 3-dmg, 4-PVP
                    var deadPlayer = PlayerList[e.number];
                    if (deadPlayer != null)
                    {
                        int penaltyAmmount = 0;
                        if (config.StaticDeathPenalty)
                            penaltyAmmount = RandomNumber(config.DeathPenaltyMin, config.DeathPenaltyMax);
                        else
                            penaltyAmmount = (int)(deadPlayer.Money * (config.DeathPenaltyPercent / 100f));
                     //   Console.WriteLine("penalty ammount: {0}", penaltyAmmount);
                        if (e.number4 == 1)
                        {
                            if (!deadPlayer.TSPlayer.Group.HasPermission("vault.bypass.death") && deadPlayer.ChangeMoney(-penaltyAmmount, MoneyEventFlags.PvP, true) && config.PvPWinnerTakesLoosersPenalty && deadPlayer.LastPVPid != -1)
                            {
                                var killer = PlayerList[deadPlayer.LastPVPid];
                                if (killer != null)
                                    killer.ChangeMoney(penaltyAmmount, MoneyEventFlags.PvP, true);
                            }
                        }
                        else if (!deadPlayer.TSPlayer.Group.HasPermission("vault.bypass.death"))
                            deadPlayer.ChangeMoney(-penaltyAmmount, MoneyEventFlags.Death,true);
                    }
                }
                else if (e.MsgId == PacketTypes.PlayerDamage)
                {
                    // Console.WriteLine("(SendData) PlayerDamage -> 1:{0} 2:{4} 3:{5} 4:{6} 5:{1} remote:{2} ignore:{3}", e.number, e.number5, e.remoteClient, e.ignoreClient, e.number2, e.number3, e.number4);
                    // 1: pID, ignore: Who, 2: dir, 3:dmg, 4:pvp;
                    if (e.number4 == 1) // if PvP
                    {
                        var player = PlayerList[e.number];
                        if (player != null)
                            player.LastPVPid = e.ignoreClient;
                    }
                }
            }            
            catch (Exception ex) { Log.ConsoleError(ex.ToString()); }
        }


        //-------------------- Static ----------------------------------------------------
        private static Vault CurrentInstance = null;
        public static event MoneyEvent OnMoneyEvent;
        public delegate void MoneyEvent(MoneyEventArgs args);

        internal static bool InvokeEvent(MoneyEventArgs args)
        {
            if (OnMoneyEvent != null)
                OnMoneyEvent(args);
            if (args.Handled)
                return true;
            return false;
        }
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
        public static bool SetBalance(string Name, int amount, bool announce = true, MoneyEventFlags flags = (MoneyEventFlags)4)
        {
            try
            {
                var player = CurrentInstance.GetPlayerByName(Name);
                if (player != null)
                {
                    int modAmount = amount - player.Money;
                    if (!player.ChangeMoney(modAmount, flags))
                        return false;
                    else if (announce)
                        player.TSPlayer.SendMessage(String.Format("Your balance has been set to {0}", MoneyToString(amount)), Color.DarkOrange);
                    return true;
                }
                else
                {
                    var Reader = CurrentInstance.Database.QueryReader("SELECT money FROM vault_players WHERE username = @0 AND worldID = @1", Name, Main.worldID);
                    if (Reader.Read())
                    {
                        MoneyEventArgs args = new MoneyEventArgs() { Amount = amount - Reader.Get<int>("money"), CurrentMoney = Reader.Get<int>("money"), PlayerName = Name, PlayerIndex = -1, EventFlags = flags };
                        Reader.Dispose();
                        if (!Vault.InvokeEvent(args))
                        {
                            CurrentInstance.Database.Query("UPDATE vault_players SET money = @0 WHERE username = @1 AND worldID = @2", amount, Name, Main.worldID);
                            return true;
                        }
                    }
                    else
                        Reader.Dispose();
                }
            }
            catch (Exception ex) { Log.ConsoleError(ex.ToString()); }
            return false;
        }
        public static bool ModifyBalance(string Name, int amount, bool announce = true, MoneyEventFlags flags = (MoneyEventFlags)4)
        {
            try
            {
                var player = CurrentInstance.GetPlayerByName(Name);
                if (player != null)
                {
                    if (player.ChangeMoney(amount, flags, announce))
                        return true;
                }
                else
                {
                    var Reader = CurrentInstance.Database.QueryReader("SELECT money FROM vault_players WHERE username = @0 AND worldID = @1", Name, Main.worldID);
                    if (Reader.Read() && Reader.Get<int>("money") >= amount * -1)
                    {
                        MoneyEventArgs args = new MoneyEventArgs() { Amount = amount, CurrentMoney = Reader.Get<int>("money"), PlayerName = Name, PlayerIndex = -1, EventFlags = flags };
                        int Newamount = Reader.Get<int>("money") + amount;
                        Reader.Dispose();
                        if (!Vault.InvokeEvent(args))
                        {
                            CurrentInstance.Database.Query("UPDATE vault_players SET money = @0 WHERE username = @1 AND worldID = @2", Newamount, Name, Main.worldID);
                            return true;
                        }
                    }
                    else
                        Reader.Dispose();
                }
            }
            catch (Exception ex) { Log.ConsoleError(ex.ToString()); }
            return false;
        }
        public static Dictionary<int, int> GetPlayerKillCounts(string Name)
        {
            Dictionary<int, int> returnDict = new Dictionary<int, int>();

            try
            {
                var player = CurrentInstance.GetPlayerByName(Name);
                if (player != null)
                    return player.KillData;
                else
                {
                    var reader = CurrentInstance.Database.QueryReader("SELECT killData FROM vault_players WHERE username = @0 AND worldID = @1", Name, Main.worldID);
                    if (reader.Read())
                        returnDict = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<int, int>>(reader.Get<string>("killData"));
                    reader.Dispose();
                }
            }
            catch (Exception ex) { Log.ConsoleError(ex.ToString()); }

            return returnDict;
        }
        public static int TotalOnlineTime(string Name)
        {
            var reader = CurrentInstance.Database.QueryReader("SELECT totalOnline FROM vault_players WHERE username = @0", Name);
            int total = 0;
            if (reader.Read())
            {
                total = reader.Get<int>("totalOnline");               
                
            }
            reader.Dispose();
            return total;
        }
        public static DateTime? LastSeenPlayer(string Name)
        {
            var reader = CurrentInstance.Database.QueryReader("SELECT lastSeen FROM vault_players WHERE username = @0", Name);  
            if (reader.Read())
            {
                DateTime lastSeen = JsonConvert.DeserializeObject<DateTime>(reader.Get<string>("lastSeen"));
                reader.Dispose();
                return lastSeen;
            }
            reader.Dispose();
            return null;
        }
        public static string MoneyToString(int amount)
        {
            if (amount == 0)
                return "0 copper";
            int[] money = MoneyToArray(Math.Abs(amount));
            StringBuilder builder = new StringBuilder(50);
            if (money[3] > 0)
                builder.AppendFormat("{0} platinum ", money[3]);
            if (money[2] > 0)
                builder.AppendFormat("{0} gold ", money[2]);
            if (money[1] > 0)
                builder.AppendFormat("{0} silver ", money[1]);
            if (money[0] > 0)
                builder.AppendFormat("{0} copper ", money[0]);
            builder.Remove(builder.Length - 1, 1);
            return builder.ToString();
        }
        public static int[] MoneyToArray(int amount)
        {
            int[] moneyArray = new int[4];
            moneyArray[0] = amount % 100;
            moneyArray[1] = amount % 10000;
            moneyArray[2] = amount % 1000000;
            moneyArray[3] = (int)Math.Floor(amount / 1000000d);
            moneyArray[2] = (int)((moneyArray[2] - moneyArray[1]) / 10000);
            moneyArray[1] = (int)((moneyArray[1] - moneyArray[0]) / 100);
            return moneyArray;
        }
        //--------------------------------------------------------------------------------

        internal class Config
        {
            public int InitialMoney = 0;
            public float BattlePotionModifier = 0.75f;
            public Dictionary<int, float> OptionalMobModifier = new Dictionary<int, float>();
            public bool GiveTimedPay = true;
            public int PayEveryMinutes = 10;
            public byte MaxIdleTime = 3;
            public int Payamount = 5000;
            public bool AnnounceTimedPay = false;
            public bool AnnounceKillGain = false;
            public bool AnnounceBossGain = true;
            public bool StaticDeathPenalty = false;
            public int DeathPenaltyMax = 10000;
            public int DeathPenaltyMin = 10000;
            public int DeathPenaltyPercent = 10;
            public bool PvPWinnerTakesLoosersPenalty = true;
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
    [Flags]
    public enum MoneyEventFlags
    {
        Kill = 0,
        Death = 1,
        PvP = 2,
        Plugin = 4,
        PayCommand = 8,
        TimedPay = 16,
        Shop = 32,
        Reward = 64
    }
    public class MoneyEventArgs : HandledEventArgs
    {
        public MoneyEventFlags EventFlags;
        public int Amount;
        public int CurrentMoney;
        public String PlayerName;
        public int PlayerIndex;
    }
}
