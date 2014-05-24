using System;
using System.IO;
using System.Timers;
using System.Text;
using System.Reflection;
using System.Collections.Generic;
using System.Data;
using System.Text.RegularExpressions;
using MySql.Data.MySqlClient;
using System.Web;
using System.Net;
using System.Diagnostics;
using System.Net.Sockets;

using PRoCon.Core;
using PRoCon.Core.Plugin;
using PRoCon.Core.Plugin.Commands;
using PRoCon.Core.Players;

namespace PRoConEvents
{
    public class pureLogServer : PRoConPluginAPI, IPRoConPluginInterface
    {
        private int pluginEnabled = 0;
        private int playerCount;
        private Timer updateTimer;
        private Timer initialTimer;
        private String mySqlHostname;
        private String mySqlPort;
        private String mySqlDatabase;
        private String mySqlUsername;
        private String mySqlPassword;
        private List<CPlayerInfo> oldPlayers;
        private List<Player> allPlayers;

        //private MySqlConnection firstConnection;
        private MySqlConnection confirmedConnection;
        //private bool SqlConnected = true;
        private String bigTableName = "bigtable";
        private String dayTableName = "daytable";
        private String bigStayTableName = "bigstaytable";
        private String dayStayTableName = "daystaytable";

        private int debugLevel = 1;
        private int backupCache = 0;
        private int backupRuns = 0;

        public pureLogServer()
        {
        }

		//Primary operations. output() is run once every minute.
        public void output(object source, ElapsedEventArgs e)
        {
            if (pluginEnabled > 0)
            {
                this.toConsole(2, "pureLog Server Tracking " + playerCount + " players online.");
                bool abortUpdate = false;

                //what time is it?
                DateTime rightNow = DateTime.Now;
                String rightNowHour = rightNow.ToString("%H");
                String rightNowMinutes = rightNow.ToString("%m");
                //Okay in retrospect this was a really dumb conversion workaround, but if it ain't broke, don't fix it.
                //This gets the time in minutes since 00:00.
                int rightNowMinTotal = (Convert.ToInt32(rightNowHour)) * 60 + Convert.ToInt32(rightNowMinutes);
                int totalPlayerCount = playerCount + backupCache;

                if (backupRuns % 5 == 0)
                {
                    //Check for a new day
                    this.goodMorning();
                    //Insert the latest interval, plus any backup cache
                    //The 'min' and 'time' columns take the 'totalPlayerCount' and 'rightNowMinTotal' values respectively.
                    //Use the connection established when the plugin was started.
                    MySqlCommand query = new MySqlCommand("INSERT INTO " + dayTableName + " (min, time) VALUES ('" + totalPlayerCount + "','" + rightNowMinTotal + "')", this.confirmedConnection);
                    if (testQueryCon(query))
                    {
                        try { query.ExecuteNonQuery(); }
                        catch (Exception m)
                        {
                            this.toConsole(1, "Couldn't parse query!");
                            this.toConsole(1, m.ToString());
                            abortUpdate = true;
                        }
                    }
                    query.Connection.Close();
                }
                else
                {
                    toConsole(2, "Skipping this day table insertion...");
                    toConsole(2, "Current backup cache value: " + this.backupCache + " // The last " + this.backupRuns + " day table insertions were skipped.");
                    this.backupCache += playerCount;
                    this.backupRuns++;
                }

                //Was the insertion a success?
                if (!abortUpdate)
                {
                    toConsole(2, "Added an interval worth " + totalPlayerCount + " for timestamp " + rightNowMinTotal);
                    //Clear out any remaining cache.
                    this.backupRuns = 0;
                    this.backupCache = 0;
                }
                else
                {
                    toConsole(1, "There's a connection problem. I'll try again in five minutes and put the next five intervals into the backup cache.");
                    //Add missing minutes to cache.
                    this.backupCache += playerCount;
                    //Consider this run skipped.
                    this.backupRuns++;
                    toConsole(2, "Current backup cache value: " + this.backupCache + " // The last " + this.backupRuns + " day table insertions were skipped.");
                }
            }
        }

        //Is it a new day? 
		//The goodMorning() function is meant to handle ALL maintenance tasks that recur with every new day.
		//It works by checking to see if a row in the bigTable exists for the current day.
        public void goodMorning()
        {
            //Get the date string values for today and yesterday.
            String dateNow = DateTime.Now.ToString("MMddyyyy");
            String dateYesterday = DateTime.Now.AddDays(-1).ToString("MMddyyyy");

            int rowCount = 999;
            //Does a row containing today's date value exist?
            MySqlCommand query = new MySqlCommand("SELECT COUNT(*) FROM " + bigTableName + " WHERE date='" + dateNow + "'", this.confirmedConnection);
            if (testQueryCon(query))
            {
                try { rowCount = int.Parse(query.ExecuteScalar().ToString()); }
                catch (Exception e)
                {
                    this.toConsole(1, "Couldn't parse query!");
                    this.toConsole(1, e.ToString());
                }
            }
            query.Connection.Close();

            if (rowCount == 0)
            {
				//There are no rows with today's date in the big table. Must be a new day!
				//Insert all tasks that run every new day here.
				
				//pureLog player-minute counter reset
                bool abortUpdate = false;

                this.toConsole(1, "Today is " + dateNow + ". Good morning!");
                this.toConsole(2, "Summing up yesterday's player minutes...");
				//Update yesterday with the content from today.
                //Calls updateBig.
                if (!updateBig(dateNow, dateYesterday))
                {
                    this.toConsole(2, "Updated yesterday's minutes!");
                    //and finally, start a new day
					//Insert a new row in the big table for today,
                    //and clear the day table.
                    query = new MySqlCommand("INSERT INTO " + bigTableName + " (date) VALUES ('" + dateNow + "'); " + "DELETE FROM " + dayTableName + "; " + "ALTER TABLE " + dayTableName + " AUTO_INCREMENT = 1;", this.confirmedConnection);
                    toConsole(3, "Executing Query: INSERT INTO " + bigTableName + " (date) VALUES ('" + dateNow + "'); " + "DELETE FROM " + dayTableName + "; " + "ALTER TABLE " + dayTableName + " AUTO_INCREMENT = 1;");
                    if (testQueryCon(query))
                    {
                        try { query.ExecuteNonQuery(); }
                        catch (Exception e)
                        {
                            this.toConsole(1, "Couldn't parse query!");
                            this.toConsole(1, e.ToString());
                            abortUpdate = true;
                        }
                    }
                    else { abortUpdate = true; }
                    query.Connection.Close();
                }
                //Insert yesterday's date and the avg from that day into bigstaytable then clear daystaytable
                query = new MySqlCommand("INSERT INTO " + bigStayTableName + " (date, avgstaytime) VALUES ('" + dateYesterday + "', (SELECT AVG(staytime) FROM " + dayStayTableName + ")); " + "DELETE FROM " + dayStayTableName + "; " + "ALTER TABLE " + dayStayTableName + " AUTO_INCREMENT = 1;", this.confirmedConnection);
            }
            else
            {
                toConsole(2, "pureLog 1.5 thinks it's the same day.");
            }
        }

        public Boolean updateBig(string dateNow, string dateYesterday)
        {
            bool abortUpdate = false;

            this.toConsole(1, "Today is " + dateNow + ". Good morning!");
            this.toConsole(2, "pureLog 1.5 New Update Function...");
            //Update yesterday's minutes...
            //Note the nested MySQL function. The min in bigTable is set to the total sum of min in the dayTable. Clever, huh?
            //emptyTime is the amount of time the server is found empty, aka the number of rows where the player count (min) is 0.
            MySqlCommand query = new MySqlCommand("UPDATE " + bigTableName + " SET min=(SELECT SUM(min) FROM " + dayTableName + "), emptyTime=(SELECT COUNT(*) FROM " + dayTableName + " WHERE min=0) WHERE date='" + dateYesterday + "'", this.confirmedConnection);
            toConsole(3, "Executing Query: " + "UPDATE " + bigTableName + " SET min=(SELECT SUM(min) FROM " + dayTableName + "), emptyTime=(SELECT COUNT(*) FROM " + dayTableName + " WHERE min=0) WHERE date='" + dateYesterday + "'");
            if (testQueryCon(query))
            {
                try { query.ExecuteNonQuery(); }
                catch (Exception e)
                {
                    this.toConsole(1, "Couldn't parse query! pureLog 1.5+ Update Function.");
                    this.toConsole(1, e.ToString());
                    abortUpdate = true;
                }
            }
            else { abortUpdate = true; }
            query.Connection.Close();
            this.toConsole(2, "pureLog 1.5+ Update Complete!");

            return abortUpdate;
        }

        public void toConsole(int msgLevel, String message)
        {
            //a message with msgLevel 1 is more important than 2
            if (debugLevel >= msgLevel)
            {
                this.ExecuteCommand("procon.protected.pluginconsole.write", "pureLogS: " + message);
            }
        }
		
		//Test the connection to see if it's valid.
        //Run this every time, just to be safe. 
        //IF CONNECTION OK, MAKE SURE TO CLOSE THE CONNECTION AFTERWARDS IN YOUR OTHER CODE!
        public bool testQueryCon(MySqlCommand theQuery)
        {
            try { theQuery.Connection.Open(); }
            catch (Exception e)
            {
                this.toConsole(1, "Couldn't open query connection!");
                this.toConsole(1, e.ToString());
                theQuery.Connection.Close();
                return false;
            }
            this.toConsole(2, "Connection OK!");
            return true;
        }

        public string GetPluginName()
        {
            return "pureLog Server Edition";
        }
        public string GetPluginVersion()
        {
            return "1.5.3";
        }
        public string GetPluginAuthor()
        {
            return "Analytalica";
        }
        public string GetPluginWebsite()
        {
            return "purebattlefield.org";
        }
        public string GetPluginDescription()
        {
            return @"<p><b>This version of pureLog is currently in development.</b>
Not all features may
be available or function properly.<br>
</p>
<p>pureLog is a MySQL database driven game-time analytics plugin
for PRoCon. Its primary function is to measure daily server popularity
by logging the collective total amount of time spent in-game by
players, designated as player-minutes. At the same time, pureLog is
capable of tracking the player-minutes of select users and can
differentiate between administrators and seeders if necessary.<br>
</p>
<p>This plugin was developed by analytalica and is currently a
PURE Battlefield exclusive.</p>
<p><big><b>What's New in pureLog 1.5+?</b></big></p>
<p>
<b>Instant On:</b> No more waiting around for
pureLog to get started launching. Disabling and re-enabling the plugin
immediately attempts a
new connection.<br>
<b>Speed Up:</b> The amount of original queries for
player-minute tracking sent by pureLog has nearly been cut in half.<br>
<b>Less Bugs:</b> Many bugs identified in pureLog 1.2 have
been fixed in 1.5+.<br>
<b>emptyTime:</b> Know how long the server at zero players is empty each day.</b>
</p>
<p><big><b>Initial Setup:</b></big><br>
</p>
<ol>
  <li>Make a new MySQL database, or choose an existing one. I
recommend starting with a new database for organizational purposes.</li>
  <li>With the database selected, run the MySQL commands as
instructed below.</li>
  <li>Use an IP address for the hostname. </li>
  <li>The default port for remote MySQL
connections is 3306 (on PURE servers, use 3603).</li>
  <li>Set the database you want this plugin to connect to.
Multiple databases will be needed for multiple servers and plugins.</li>
  <li>Provide a username and password combination with the
permissions (SELECT, INSERT, UPDATE, DELETE, ALTER) necessary to
access that
database.</li>
  <li>The debug levels are as follows: 0
suppresses ALL messages (not recommended), 1 shows important messages
only (recommended), and 2 shows ALL messages (useful for step by step
debugging).</li>
  <li>Set the table names to the same names chosen in steps 1
and 2.</li>
  <ol>
  </ol>
</ol>
<p><b>MySQL Commands:</b><br>
</p>
<p>
<ul>
  <li>CREATE TABLE IF NOT EXISTS bigtable(id int NOT NULL
AUTO_INCREMENT, date varchar(255), min int(11), emptyTime int(11),
PRIMARY KEY (id));</li>
  <li>CREATE TABLE IF NOT EXISTS daytable(id int NOT NULL
AUTO_INCREMENT, time varchar(255), min int(11), PRIMARY KEY (id));</li>
</ul></p>
<p>
Because the database name varies, you will need to manually
select it ('USE database_name') before running the queries.
</p>
<p><big><b>How it Works:</b></big></p>
<p>Every row in the Big Table stands for a different day, as
indicated by the timestamp found in the date column. The Big Table's
min column stores the total amount of minutes players spent in game
that day. On a 64-player server, there is a maximum of 60*24*64 = 92160
in-game minutes possible per day. The emptyTime column records the
amount of minutes the server is empty at zero players.</p>
<p>Every row in the Day Table stands for a different interval
(typically polled every minute), as indicated by the timestamp found in
the time column. The Day Table's min column stores the amount of
players recorded during that time interval. At the beginning of each
new day, the total sum of all the intervals is inserted into the Big
Table as an entry for the previous day, and then the Day Table is reset.</p>
<p><big><b>Troubleshooting: </b></big><br>
</p>
<ul>
  <li>The IP address that Procon uses to access the MySQL
server
may be different from the IP of the layer itself. If a remote
connection
can't be established, try using more wildcards in the accepted
connections (do %.%.%.% to test).</li>
  <li>(Fixed as of pureLog 1.3.8) There is an error message
that
always appears when
initializing pureLog on a new database. It can be safely ignored and
should disappear in the next minute.</li>
</ul>
<p><big><b>Fallbacks: </b></big><br>
</p>
<ul>
  <li>If at any step in the table updating process the
connection
fails, the plugin will continue adding minutes to the most recent day.
A new day for the plugin only begins when the connection is successful.
  </li>
  <li>On the case of a MySQL connection failure, the plugin
will
skip the next five insertion attempts to avoid overloading PRoCon and
the server.
Missing intervals will be summed up into one when a connection is
re-established.</li>
  <li>If an initial connection can't be established, the plugin
will try again once every minute. All error messages will be shown in
the console output with debug level set to 1.</li>
</ul>


";
        }

        public void OnPluginLoaded(string strHostName, string strPort, string strPRoConVersion)
        {
            //this.RegisterEvents(this.GetType().Name, "OnServerInfo", "OnListPlayers");
            this.RegisterEvents(this.GetType().Name, "OnPluginLoaded", "OnServerInfo", "OnListPlayers");
            this.ExecuteCommand("procon.protected.pluginconsole.write", "pureLog Server Edition Loaded!");
        }

        public void OnListPlayers(List<CPlayerInfo> players, CPlayerSubset subset)
        {
            //if a player in the newest playerList is not in the all playerlist, add them to all playerlist
            for (int i = 0; i < players.Count; i++)
                if (playersListContains(players[i]) == -1)
                    allPlayers.Add(players[i]);
            try
            {
                
                //if a player is in the last player list, but not int the newest, they left. find out staytime and add their name to the daystaytable then remove them from the all players
                for (int i = 0; i < this.oldPlayers.Count; i++)
                    if (playersListContain(this.oldPlayers[i], players) == -1)
                    {
                        int index = playersListContains(this.oldPlayers[i]);
                        TimeSpan time = allPlayers[index].end();
                        if (time.TotalSeconds >= 30)
                            query = new MySqlCommand("INSERT INTO " + dayStayTableName + " ( '" + "SAM" + "', " + staytime + " ) VALUES ( '" + allPlayers[index].CPlayerInfo.SoldierName + "', " + time.TotalSeconds + " )", this.confirmedConnection);
                        allPlayers.Remove(index);
                    }
                //make the most recent playerlist the older playerlist so that next time OnListPlayers is run, it will use the playerlist from the last call of OnListPlayers
                this.oldPlayers = players;
            }//catch if oldPlayers is null
            catch (Exception e)
            {
                this.oldPlayers = players;
            }
        }

        //check if player is in players
        private int playersListContains(CPlayerInfo player, List<CPlayerInfo> players)
        {
            for (int i = 0; i < allPlayers.Count; i++)
                if (players[i].SoldierName == player.SoldierName)
                    return i;
            return -1;
        }

        //check if player is in allPlayers
        private int playersListContains(CPlayerInfo player)
        {
            for (int i = 0; i < allPlayers.Count; i++)
                if (allPlayers[i].CPlayerInfo.SoldierName == player.SoldierName)
                    return i;
            return -1;
        }

        public void OnPluginEnable()
        {
            this.pluginEnabled = 1;
            this.toConsole(1, "pureLog Server Edition Running!");
            this.toConsole(2, "The plugin will try and connect once every minute. Please wait...");

            //pureLog 2.0: Set an update timer, but try establishing the first connection immediately.
            this.initialTimer = new Timer();
            this.initialTimer.Elapsed += new ElapsedEventHandler(this.establishFirstConnection);
			//Run the function "establishFirstConnection" in two seconds.
            this.initialTimer.Interval = 2000;
            this.initialTimer.Start();
        }
		
        //The first thing the plugin does.
        public void establishFirstConnection(object source, ElapsedEventArgs e)
        {
			//Run this again in 60 seconds IF it fails the first time.
            this.initialTimer.Interval = 60000;
            bool SqlConnected = true;
            this.toConsole(2, "Trying to connect to " + mySqlHostname + ":" + mySqlPort + " with username " + mySqlUsername);
            MySqlConnection firstConnection = new MySqlConnection("Server=" + mySqlHostname + ";" + "Port=" + mySqlPort + ";" + "Database=" + mySqlDatabase + ";" + "Uid=" + mySqlUsername + ";" + "Pwd=" + mySqlPassword + ";" + "Connection Timeout=5;");
            try { firstConnection.Open(); }
            catch (Exception z)
            {
                this.toConsole(1, "Initial connection error!");
                this.toConsole(1, z.ToString());
                SqlConnected = false;
            }
            //Get ready to rock!
            if (SqlConnected)
            {
                firstConnection.Close();
                this.toConsole(1, "Connection established with " + mySqlHostname + "!");
                this.toConsole(2, "Stopping connection retry attempts timer...");
				//Stop the timer that attempts connections.
                this.initialTimer.Stop();
                this.confirmedConnection = firstConnection;
                this.updateTimer = new Timer();
                this.updateTimer.Elapsed += new ElapsedEventHandler(this.output);
                this.updateTimer.Interval = 60000;
                this.updateTimer.Start();
                //this.output();
            }
            else
            {
                this.toConsole(1, "Could not establish an initial connection. I'll try again in a minute.");
            }
        }

        public void OnPluginDisable()
        {
            this.pluginEnabled = 0;
			//Does this actually do anything? I dunno.
            this.ExecuteCommand("procon.protected.tasks.remove", "pureLogServer");
            this.toConsole(2, "Stopping connection retry attempts timer...");
            this.initialTimer.Stop();
            this.toConsole(2, "Stopping update timer...");
            this.updateTimer.Stop();
            this.toConsole(1, "pureLog Server Edition Closed.");
        }

        public override void OnServerInfo(CServerInfo csiServerInfo)
        {
            this.playerCount = csiServerInfo.PlayerCount;
        }

        //public override void OnListPlayers(List<CPlayerInfo> players, CPlayerSubset subset)
        //{
        //    toConsole(3, "OnListPlayers");
        //    foreach (CPlayerInfo player in players)
        //    {
        //        toConsole(3, "Printing names");
        //        toConsole(3, player.SoldierName);
        //    }
        //}

        public List<CPluginVariable> GetDisplayPluginVariables()
        {
            List<CPluginVariable> lstReturn = new List<CPluginVariable>();
            //MySQL connection info.
            lstReturn.Add(new CPluginVariable("MySQL Settings|MySQL Hostname", typeof(string), mySqlHostname));
            lstReturn.Add(new CPluginVariable("MySQL Settings|MySQL Port", typeof(string), mySqlPort));
            lstReturn.Add(new CPluginVariable("MySQL Settings|MySQL Database", typeof(string), mySqlDatabase));
            lstReturn.Add(new CPluginVariable("MySQL Settings|MySQL Username", typeof(string), mySqlUsername));
            lstReturn.Add(new CPluginVariable("MySQL Settings|MySQL Password", typeof(string), mySqlPassword));
            //Table info.
            lstReturn.Add(new CPluginVariable("Table Names|Big Table", typeof(string), bigTableName));
            lstReturn.Add(new CPluginVariable("Table Names|Day Table", typeof(string), dayTableName));
            lstReturn.Add(new CPluginVariable("Other|Debug Level", typeof(string), debugLevel.ToString()));
            return lstReturn;
        }

        public List<CPluginVariable> GetPluginVariables()
        {
            return GetDisplayPluginVariables();
        }

        public void SetPluginVariable(string strVariable, string strValue)
        {
            if (strVariable.Contains("MySQL Hostname"))
            {
                mySqlHostname = strValue;
            }
            else if (strVariable.Contains("MySQL Port"))
            {
                int tmp = 3306;
                int.TryParse(strValue, out tmp);
                if (tmp > 0 && tmp < 65536)
                {
                    mySqlPort = strValue;
                }
                else
                {
                    this.ExecuteCommand("procon.protected.pluginconsole.write", "Invalid SQL Port Value.");
                }
            }
            else if (strVariable.Contains("MySQL Database"))
            {
                mySqlDatabase = strValue.Trim();
            }
            else if (strVariable.Contains("MySQL Username"))
            {
                mySqlUsername = strValue.Trim();
            }
            else if (strVariable.Contains("MySQL Password"))
            {
                mySqlPassword = strValue.Trim();
            }
            else if (strVariable.Contains("Big Table"))
            {
                bigTableName = strValue.Trim();
            }
            else if (strVariable.Contains("Day Table"))
            {
                dayTableName = strValue.Trim();
            }
            else if (strVariable.Contains("Debug Level"))
            {
                try
                {
                    debugLevel = Int32.Parse(strValue);
                }
                catch (Exception z)
                {
                    toConsole(1, "Invalid debug level! Choose 0, 1, or 2 only.");
                    debugLevel = 1;
                }
            }
        }
    }

    class Player
    {
        public CPlayerInfo player;
        public DateTime start;
        public DateTime end;

        public Player(CPlayerInfo player) {
            this.player = player;
            start = DateTime.UtcNow;
        }

        public TimeSpan end()
        {
            end = DateTime.UtcNow;
            return end - start;
        }
    }
}