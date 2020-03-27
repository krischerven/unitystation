﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;
using Telepathy;
using Debug = UnityEngine.Debug;

namespace AdminTools
{
	//A serverside optional auto moderator to help make
	//server admin work easier. Only works in headless mode
	public class AutoMod : MonoBehaviour
	{
		private static AutoMod autoMod;

		public static AutoMod Instance
		{
			get
			{
				if (autoMod == null)
				{
					autoMod = FindObjectOfType<AutoMod>();
				}

				return autoMod;
			}
		}

		//Cooldown is based on a score system. A score is created every time a user posts a chat message. It will check how many
		//times the user has posted something, how fast and compare the content. If the resulting score is higher then the max score
		//them AutoMod will take action to stop the spamming
		private static Dictionary<ConnectedPlayer, MessageRecord> chatLogs =
			new Dictionary<ConnectedPlayer, MessageRecord>();

		private static float maxScore = 0.7f; //0 - 1f;

		private AutoModConfig loadedConfig;

		private static string AutoModConfigPath =>
			Path.Combine(Application.streamingAssetsPath, "admin", "automodconfig.json");

		private void Start()
		{
			LoadConfig();
		}

		private void SaveConfig()
		{
			if (loadedConfig == null) return;

			File.WriteAllText(AutoModConfigPath, JsonUtility.ToJson(loadedConfig));
		}

		private void LoadConfig()
		{
			if (File.Exists(AutoModConfigPath))
			{
				var config = File.ReadAllText(AutoModConfigPath);
				loadedConfig = JsonUtility.FromJson<AutoModConfig>(config);
				Logger.Log("Successfully loaded Auto Mod config");
			}
		}

		void Update()
		{
			if (!IsEnabled()) return;
			MonitorEnvironment();
		}

		void MonitorEnvironment()
		{
			if (Common.allocationAttackQueue.Count > 0)
			{
				ProcessAllocationAttack(Common.allocationAttackQueue.Dequeue());
			}
		}

		public static void ProcessAllocationAttack(string ipAddress)
		{
			if (!Instance.loadedConfig.enableAllocationProtection) return;
			if (Application.platform == RuntimePlatform.LinuxPlayer)
			{
				Logger.Log($"Auto mod has taken steps to protect against an allocation attack from {ipAddress}");
				ProcessStartInfo processInfo = new ProcessStartInfo();
				processInfo.FileName = "ufw";
				processInfo.Arguments = $"insert 1 deny from {ipAddress} to any";
				processInfo.CreateNoWindow = true;
				processInfo.UseShellExecute = false;
				Process.Start(processInfo);
			}
		}

		public static string ProcessChatServer(ConnectedPlayer player, string message)
		{
			if (player == null || Instance.loadedConfig == null
			                   || !Instance.loadedConfig.enableSpamProtection) return message;

			if (!chatLogs.ContainsKey(player))
			{
				chatLogs.Add(player, new MessageRecord
				{
					player = player
				});
			}

			if (chatLogs[player].IsSpamming(message))
			{
				return "";
			}

			return message;
		}

		private static bool IsEnabled()
		{
			if (Instance == null || !GameData.IsHeadlessServer || Instance.loadedConfig == null) return false;
			if (!Instance.loadedConfig.enableAutoMod) return false;
			return true;
		}

		[ContextMenu("Create default config file")]
		void CreateDefaultConfig()
		{
			File.WriteAllText(AutoModConfigPath, JsonUtility.ToJson(new AutoModConfig()));
		}

		class MuteRecord
		{
			public DateTime timeOfMute;
			public int lengthOfMute;

			public int RemainingSeconds()
			{
				var remainingSeconds = lengthOfMute - ((int) (DateTime.Now - timeOfMute).TotalSeconds);
				return remainingSeconds;
			}
		}

		class MessageRecord
		{
			private Dictionary<DateTime, string> messageLog = new Dictionary<DateTime, string>();
			private List<MuteRecord> muteRecords = new List<MuteRecord>();
			public ConnectedPlayer player;

			public bool IsSpamming(string message)
			{
				if (muteRecords.Count != 0)
				{
					var remainingSeconds = muteRecords[muteRecords.Count - 1].RemainingSeconds();
					if (remainingSeconds > 0)
					{
						SendMuteMessageToPlayer(remainingSeconds);
						return true;
					}
				}

				messageLog.Add(DateTime.Now, message);
				if (CalculateSpamScore() > maxScore)
				{
					AddMuteRecord();
					return true;
				}

				return false;
			}

			private void AddMuteRecord()
			{
				var record = new MuteRecord
				{
					timeOfMute = DateTime.Now,
					lengthOfMute = 5 * (muteRecords.Count + 1)
				};
				muteRecords.Add(record);
				//clear them so we can start the spam
				//checks on a clean slate when they are unmuted
				messageLog.Clear();

				SendMuteMessageToPlayer(record.lengthOfMute);
			}

			private void SendMuteMessageToPlayer(int remainingSeconds)
			{
				if (player.GameObject != null)
				{
					Chat.AddExamineMsgFromServer(player.GameObject,
						$"You are doing that too often. Please wait {remainingSeconds} seconds");
				}
			}

			private float CalculateSpamScore()
			{
				float currentScore = 0f;
				int repeatMessages = 0;
				for (int i = messageLog.Count - 1;
					i >= 0 && i >= messageLog.Count - 6;
					i--)
				{
					int prevIndex = i - 1;
					if (prevIndex < 0) break;

					var thisKvp = messageLog.ElementAt(i);
					var prevKvp = messageLog.ElementAt(prevIndex);
					var totalSeconds = (thisKvp.Key
					                    - prevKvp.Key).TotalSeconds;

					if (totalSeconds < 1f)
					{
						currentScore += 0.2f;
						if (thisKvp.Value == prevKvp.Value)
						{
							currentScore += 0.2f;
							repeatMessages++;
						}
					}

					if (repeatMessages >= 4)
					{
						currentScore += 0.8f;
					}
				}

				return Mathf.Clamp(currentScore, 0f, 1f);
			}
		}
	}

	[Serializable]
	public class AutoModConfig
	{
		public bool enableAutoMod = true;
		public bool enableAllocationProtection = true;
		public bool enableSpamProtection = true;
		public bool enableBadWordFilter = true;
		public bool enableRdmNotification = true;
		public bool enablePlasmaReleaseNotification = true;
	}
}