﻿#define UsingBotwinderSecure

using System;
using System.Threading.Tasks;
using Botwinder.core;
using Botwinder.entities;
using Botwinder.modules;
#if UsingBotwinderSecure
using Botwinder.secure;
#endif

using guid = System.Int64;

namespace Botwinder.discord
{
	class Program
	{
		static void Main(string[] args)
		{
			(new Client()).RunAndWait();
		}
	}

	class Client
	{
		private BotwinderClient<UserData> Bot;


		public Client()
		{}

		public async Task RunAndWait()
		{
			this.Bot = new BotwinderClient<UserData>();
			Console.WriteLine(await this.Bot.TestDb());
		}
	}
}
