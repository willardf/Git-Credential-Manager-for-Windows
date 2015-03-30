﻿using Microsoft.IdentityModel.Clients.ActiveDirectory;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Microsoft.TeamFoundation.Git.Helpers.Authentication
{
    public abstract class BaseVsoAuthentication : BaseAuthentication
    {
        public static readonly string DefaultResource = "499b84ac-1321-427f-aa17-267ca6975798";
        public static readonly Guid DefaultClientId = new Guid("872cd9fa-d31f-45e0-9eab-6e460a02d1f1");
        public const string RedirectUrl = "urn:ietf:wg:oauth:2.0:oob";

        protected const string SecondaryCredentialPrefix = "alt-git";
        protected const string TokenPrefix = "adal-refresh";

        protected BaseVsoAuthentication(string authorityHostUrl)
        {
            //AdalTrace.TraceSource.Switch.Level = SourceLevels.Off;
            //AdalTrace.LegacyTraceSwitch.Level = TraceLevel.Off;

            this.AuthorityHostUrl = authorityHostUrl;
            this.ClientId = DefaultClientId;
            this.Resource = DefaultResource;
            this.PersonalAccessTokenStore = new CredentialStore(PrimaryCredentialPrefix);
            this.UserCredentialStore = new CredentialStore(SecondaryCredentialPrefix);
            this.AdaRefreshTokenStore = new TokenStore(TokenPrefix);
            this.PersonalAccessTokenCache = new CredentialCache(PrimaryCredentialPrefix);
        }
        protected BaseVsoAuthentication(string authorityHostUrl, string resource, Guid clientId)
            : this(authorityHostUrl)
        {

            this.ClientId = clientId;
            this.Resource = resource;
        }
        internal BaseVsoAuthentication(string authorityHostUrl, ICredentialStore personalAccessToken, ICredentialStore userCredential, ITokenStore adaRefresh)
            : this(authorityHostUrl)
        {
            this.PersonalAccessTokenStore = personalAccessToken;
            this.UserCredentialStore = userCredential;
            this.AdaRefreshTokenStore = adaRefresh;
        }

        public readonly string AuthorityHostUrl;
        public readonly Guid ClientId;
        public readonly string Resource;

        protected ICredentialStore PersonalAccessTokenStore { get; set; }
        protected ICredentialStore UserCredentialStore { get; set; }
        protected ITokenStore AdaRefreshTokenStore { get; set; }
        protected ICredentialStore PersonalAccessTokenCache { get; set; }

        public override void DeleteCredentials(Uri targetUri)
        {
            BaseSecureStore.ValidateTargetUri(targetUri);

            Credential credentials = null;
            Token token = null;
            if (this.PersonalAccessTokenStore.ReadCredentials(targetUri, out credentials))
            {
                this.PersonalAccessTokenCache.DeleteCredentials(targetUri);
                this.PersonalAccessTokenStore.DeleteCredentials(targetUri);
            }
            else if (this.AdaRefreshTokenStore.ReadToken(targetUri, out token))
            {
                this.AdaRefreshTokenStore.DeleteToken(targetUri);
            }
            else if (this.UserCredentialStore.ReadCredentials(targetUri, out credentials))
            {
                this.UserCredentialStore.DeleteCredentials(targetUri);
            }
        }

        public override bool GetCredentials(Uri targetUri, out Credential credentials)
        {
            BaseSecureStore.ValidateTargetUri(targetUri);

            Trace.TraceInformation("Attempting to retrieve cached credentials");

            // check the in-memory cache first
            if (!this.PersonalAccessTokenCache.ReadCredentials(targetUri, out credentials))
            {
                Trace.TraceInformation("Unable to retrieve cached credentials, attempting stored credentials retrieval.");

                // fall-back to the on disk cache
                if (this.PersonalAccessTokenStore.ReadCredentials(targetUri, out credentials))
                {
                    Trace.TraceInformation("Successfully retrieved stored credentials, updating credential cache");

                    // update the in-memory cache for faster future look-ups
                    this.PersonalAccessTokenCache.WriteCredentials(targetUri, credentials);
                }
            }

            return credentials != null;
        }

        public abstract Task<bool> RefreshCredentials(Uri targetUri);

        public bool RequestUserCredentials(Uri targetUri, out Credential credentials)
        {
            BaseSecureStore.ValidateTargetUri(targetUri);

            return this.UserCredentialStore.PromptUserCredentials(targetUri, out credentials);
        }

        public async Task<bool> ValidateCredentials(Credential credentials)
        {
            const string VsoValidationUrl = "https://app.vssps.visualstudio.com/_apis/profile/profiles/me?api-version=1.0";

            Credential.Validate(credentials);

            try
            {
                string basicAuthHeader = "Basic " + Convert.ToBase64String(ASCIIEncoding.ASCII.GetBytes(String.Format("{0}:{1}", credentials.Username, credentials.Password)));
                HttpWebRequest request = WebRequest.CreateHttp(VsoValidationUrl);
                request.Headers.Add(HttpRequestHeader.Authorization, basicAuthHeader);
                HttpWebResponse response = await request.GetResponseAsync() as HttpWebResponse;
                Trace.TraceInformation("validation status code: {0}", response.StatusCode);
                return response.StatusCode == HttpStatusCode.OK;
            }
            catch (Exception exception)
            {
                Debug.WriteLine(exception);
            }

            return false;
        }

        protected async Task<bool> GeneratePersonalAccessToken(Uri targetUri, AuthenticationResult authResult)
        {
            const string VsspEndPointUrl = "https://app.vssps.visualstudio.com/_apis/token/sessiontokens?api-version=1.0&tokentype=compact";

            Debug.Assert(targetUri != null, "The targetUri parameter is null");
            Debug.Assert(authResult != null, "The authResult parameter is null");

            Trace.TraceInformation("Generationg Personal Access Token for {0}", targetUri);

            try
            {
                using (HttpClient httpClient = new HttpClient())
                {
                    StringContent content = new StringContent(String.Empty, Encoding.UTF8, "application/json");
                    httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(authResult.AccessTokenType, authResult.AccessToken);

                    HttpResponseMessage response = await httpClient.PostAsync(VsspEndPointUrl, content);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        string responseText = await response.Content.ReadAsStringAsync();
                        Trace.TraceInformation("PAT Server response:\n{0}", responseText);

                        Match tokenMatch = null;
                        if ((tokenMatch = Regex.Match(responseText, @"\s*""token""\s*:\s*""(\S+)""\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase)).Success)
                        {
                            string token = tokenMatch.Groups[1].Value;
                            Credential personalAccessToken = new Credential(token, String.Empty);
                            this.PersonalAccessTokenStore.WriteCredentials(targetUri, personalAccessToken);
                            this.PersonalAccessTokenCache.WriteCredentials(targetUri, personalAccessToken);
                            return true;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.WriteLine(exception);
            }

            return false;
        }

        protected void StoreRefreshToken(Uri targetUri, AuthenticationResult authResult)
        {
            Debug.Assert(targetUri != null, "The targetUri parameter is null");
            Debug.Assert(authResult != null, "The authResult parameter is null");

            Trace.TraceInformation("Storing refresh token: {0}", authResult.RefreshToken);

            Token refreshToken = new Token(authResult.RefreshToken, authResult.ExpiresOn);
            this.AdaRefreshTokenStore.WriteToken(targetUri, refreshToken);
        }
    }
}