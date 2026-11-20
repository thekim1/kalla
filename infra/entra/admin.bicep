// kalla-admin: the RAG admin BFF's own registration (plans/identity/design.md §1.6, §12.1), configured like the
// gateway's admin registration (docs/identity-and-roles.md, Entra ID steps 1-5) plus the rag-* roles, the group claim
// and the acct claim. Its client secret is created by an operator, not here (§12.2).
extension microsoftGraphV1

param ragAdminHost string
param acceptMappedClaims bool
param ownerIds string[]

var graph = loadJsonContent('graph.json')

// App role ids must never change once assigned, so they are derived from the role value, not generated per run. A role
// is retired by deploying it with isEnabled false first; only then can it be removed.
var roles = [
  { value: 'rag-platform-admin', displayName: 'RAG platform admin', description: 'Ume.Rag: everything' }
  { value: 'rag-department-admin', displayName: 'RAG department admin', description: 'Ume.Rag: own departments' }
  { value: 'rag-pipeline-editor', displayName: 'RAG pipeline editor', description: 'Ume.Rag: pipelines' }
  {
    value: 'rag-reviewer'
    displayName: 'RAG reviewer'
    description: 'Ume.Rag: feedback comments and conversations with a reason'
  }
  { value: 'rag-viewer', displayName: 'RAG viewer', description: 'Ume.Rag: read' }
  {
    value: 'rag-editor'
    displayName: 'RAG editor'
    description: 'Ume.Rag: assistants and linked skills in own departments (P2)'
  }
  { value: 'rag-skill-editor', displayName: 'RAG skill editor', description: 'Ume.Rag: skills in own departments (P2)' }
]

var appRoles = [
  for role in roles: {
    id: guid('kalla-admin', role.value)
    value: role.value
    displayName: role.displayName
    description: role.description
    allowedMemberTypes: ['User']
    isEnabled: true
  }
]

resource app 'Microsoft.Graph/applications@v1.0' = {
  uniqueName: 'kalla-admin'
  displayName: 'Umeå kommun – Ume.Rag admin'
  signInAudience: 'AzureADMyOrg'
  web: {
    redirectUris: [
      'https://${ragAdminHost}/signin-oidc'
      'https://${ragAdminHost}/signout-callback-oidc'
    ]
    implicitGrantSettings: {
      enableAccessTokenIssuance: false
      enableIdTokenIssuance: false
    }
  }
  appRoles: appRoles
  // Object ids of the user's security groups in the ID token's groups claim, for member groups (§2.5).
  groupMembershipClaims: 'SecurityGroup'
  optionalClaims: {
    idToken: [
      { name: 'email' }
      { name: 'acct' }
    ]
  }
  api: {
    acceptMappedClaims: acceptMappedClaims
  }
  requiredResourceAccess: [graph.signInScopes]
  owners: {
    relationships: ownerIds
  }
}

resource servicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: app.appId
  appRoleAssignmentRequired: true
  // Lists the app under Enterprise applications, where the role assignments are made.
  tags: ['WindowsAzureActiveDirectoryIntegratedApp']
  owners: {
    relationships: ownerIds
  }
}

output appId string = app.appId
output servicePrincipalId string = servicePrincipal.id
output appRoles array = [for role in appRoles: { value: role.value, id: role.id }]
