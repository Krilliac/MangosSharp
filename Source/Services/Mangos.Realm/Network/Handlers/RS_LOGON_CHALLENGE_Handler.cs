﻿using Mangos.Common.Enums.Global;
using Mangos.Common.Globals;
using Mangos.Loggers;
using Mangos.Realm.Models;
using Mangos.Realm.Network.Readers;
using Mangos.Realm.Network.Responses;
using Mangos.Realm.Network.Writers;
using Mangos.Realm.Storage.Entities;
using Mangos.Storage.Account;
using System.Globalization;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Mangos.Realm.Network.Handlers
{
    public class RS_LOGON_CHALLENGE_Handler : IPacketHandler
    {
        private readonly ILogger logger;
        private readonly MangosGlobalConstants mangosGlobalConstants;
        private readonly IRealmStorage realmStorage;

        private readonly AUTH_LOGON_PROOF_Writer AUTH_LOGON_PROOF_Writer;
        private readonly RS_LOGON_CHALLENGE_Reader RS_LOGON_CHALLENGE_Reader;
        private readonly AUTH_LOGON_CHALLENGE_Writer AUTH_LOGON_CHALLENGE_Writer;

        public RS_LOGON_CHALLENGE_Handler(
            ILogger logger,
            MangosGlobalConstants mangosGlobalConstants,
            IRealmStorage realmStorage,
            AUTH_LOGON_PROOF_Writer AUTH_LOGON_PROOF_Writer,
            RS_LOGON_CHALLENGE_Reader RS_LOGON_CHALLENGE_Reader, 
            AUTH_LOGON_CHALLENGE_Writer AUTH_LOGON_CHALLENGE_Writer)
        {
            this.mangosGlobalConstants = mangosGlobalConstants;
            this.realmStorage = realmStorage;
            this.logger = logger;
            this.AUTH_LOGON_PROOF_Writer = AUTH_LOGON_PROOF_Writer;
            this.RS_LOGON_CHALLENGE_Reader = RS_LOGON_CHALLENGE_Reader;
            this.AUTH_LOGON_CHALLENGE_Writer = AUTH_LOGON_CHALLENGE_Writer;
        }

        public async Task HandleAsync(ChannelReader<byte> reader, ChannelWriter<byte> writer, ClientModel clientModel)
        {
            var request = await RS_LOGON_CHALLENGE_Reader.ReadAsync(reader);
            clientModel.AccountName = request.AccountName;

            // DONE: Check if our build can join the server
            // If ((RequiredVersion1 = 0 AndAlso RequiredVersion2 = 0 AndAlso RequiredVersion3 = 0) OrElse
            // (bMajor = RequiredVersion1 AndAlso bMinor = RequiredVersion2 AndAlso bRevision = RequiredVersion3)) AndAlso
            // clientBuild >= RequiredBuildLow AndAlso clientBuild <= RequiredBuildHigh Then
            if (request.Build == mangosGlobalConstants.Required_Build_1_12_1 
                | request.Build == mangosGlobalConstants.Required_Build_1_12_2 
                | request.Build == mangosGlobalConstants.Required_Build_1_12_3)
            {
                // TODO: in the far future should check if the account is expired too                
                var accountInfo = await realmStorage.GetAccountInfoAsync(clientModel.AccountName);
                var accountState = await GetAccountStateAsync(accountInfo);

                // DONE: Send results to client
                switch (accountState)
                {
                    case AccountState.LOGIN_OK:
                        {
                            if (accountInfo.sha_pass_hash.Length != 40) // Invalid password type, should always be 40 characters
                            {
                                await AUTH_LOGON_PROOF_Writer.WriteAsync(writer, new AUTH_LOGON_PROOF(AccountState.LOGIN_BAD_PASS));
                            }
                            else // Bail out with something meaningful
                            {
                                var hash = GetPasswordHashFromString(accountInfo.sha_pass_hash);

                                // Language = clientLanguage
                                // If Not IsDBNull(result.Rows(0).Item("expansion")) Then
                                // Expansion = result.Rows(0).Item("expansion")
                                // Else
                                // Expansion = ExpansionLevel.NORMAL
                                // End If

                                clientModel.ClientAuthEngine.CalculateX(request.Account, hash);

                                await AUTH_LOGON_CHALLENGE_Writer.WriteAsync(writer, new AUTH_LOGON_CHALLENGE(
                                    clientModel.ClientAuthEngine.PublicB,
                                    clientModel.ClientAuthEngine.g,
                                    clientModel.ClientAuthEngine.N,
                                    clientModel.ClientAuthEngine.Salt,
                                    ClientAuthEngine.CrcSalt
                                    ));
                            }
                            return;
                        }

                    case AccountState.LOGIN_UNKNOWN_ACCOUNT:
                        await AUTH_LOGON_PROOF_Writer.WriteAsync(writer, new AUTH_LOGON_PROOF(AccountState.LOGIN_UNKNOWN_ACCOUNT));
                        return;

                    case AccountState.LOGIN_BANNED:
                        await AUTH_LOGON_PROOF_Writer.WriteAsync(writer, new AUTH_LOGON_PROOF(AccountState.LOGIN_BANNED));
                        return;

                    case AccountState.LOGIN_NOTIME:
                        await AUTH_LOGON_PROOF_Writer.WriteAsync(writer, new AUTH_LOGON_PROOF(AccountState.LOGIN_NOTIME));
                        return;

                    case AccountState.LOGIN_ALREADYONLINE:
                        await AUTH_LOGON_PROOF_Writer.WriteAsync(writer, new AUTH_LOGON_PROOF(AccountState.LOGIN_ALREADYONLINE));
                        return;

                    case AccountState.LOGIN_FAILED:
                    case AccountState.LOGIN_BAD_PASS:
                    case AccountState.LOGIN_DBBUSY:     
                    case AccountState.LOGIN_BADVERSION:
                    case AccountState.LOGIN_DOWNLOADFILE:
                    case AccountState.LOGIN_SUSPENDED:
                    case AccountState.LOGIN_PARENTALCONTROL:
                        break;

                    default:
                        await AUTH_LOGON_PROOF_Writer.WriteAsync(writer, new AUTH_LOGON_PROOF(AccountState.LOGIN_FAILED));
                        return;
                }
            }
            else
            {
                // Send BAD_VERSION
                logger.Warning($"WRONG_VERSION {request.Build}");
                await AUTH_LOGON_PROOF_Writer.WriteAsync(writer, new AUTH_LOGON_PROOF(AccountState.LOGIN_BADVERSION));
            }
        }

        private async Task<AccountState> GetAccountStateAsync(AccountInfoEntity accountInfo)
        {
            if (accountInfo != null)
            {
                return await realmStorage.IsBannedAccountAsync(accountInfo.id)
                    ? AccountState.LOGIN_BANNED
                    : AccountState.LOGIN_OK;
            }
            else
            {
                return AccountState.LOGIN_UNKNOWN_ACCOUNT;
            }
        }

        private byte[] GetPasswordHashFromString(string sha_pass_hash)
        {
            var hash = new byte[20];
            for (int i = 0; i < 40; i += 2)
            {
                hash[i / 2] = byte.Parse(sha_pass_hash.Substring(i, 2), NumberStyles.HexNumber);
            }
            return hash;
        }
    }
}