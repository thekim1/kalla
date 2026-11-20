// kalla-open-webui: the pilot Open WebUI's sign-in registration (plans/identity/design.md §12.1). Its client secret
// (OAUTH_CLIENT_SECRET) is created by an operator, not here (§12.2). Access is limited to the pilot users group through
// appRoleAssignmentRequired and a default-access assignment made by a person.
extension microsoftGraphV1

param openWebUiHost string
param ownerIds string[]

var graph = loadJsonContent('graph.json')

resource app 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: 'kalla-open-webui'
  displayName: 'Umeå kommun – Källa Open WebUI'
  signInAudience: 'AzureADMyOrg'
  web: {
    redirectUris: [
      'https://${openWebUiHost}/oauth/oidc/login/callback'
    ]
    implicitGrantSettings: {
      enableAccessTokenIssuance: false
      enableIdTokenIssuance: false
    }
  }
  optionalClaims: {
    idToken: [
      { name: 'email' }
      { name: 'acct' }
    ]
  }
  requiredResourceAccess: [graph.signInScopes]
  owners: {
    relationships: ownerIds
  }
}

resource servicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: app.appId
  appRoleAssignmentRequired: true
  tags: ['WindowsAzureActiveDirectoryIntegratedApp']
  owners: {
    relationships: ownerIds
  }
}

output appId string = app.appId
output servicePrincipalId string = servicePrincipal.id
