// Entra approval package 1 (plans/identity/design.md §12): RAG's own registrations for admin and Open WebUI sign-in,
// and the pilot groups. Never declares or touches the gateway's registration (§12.2). Client secrets, admin consent,
// group -> app role assignments and group membership are manual steps (README.md), not resources here.
extension microsoftGraphV1

@description('Host name of the RAG admin UI, without scheme or path, e.g. kalla-admin.example.se.')
param ragAdminHost string

@description('Host name of the pilot Open WebUI, without scheme or path.')
param openWebUiHost string

@description('Display name of the pilot department, used in the department admins group name.')
param pilotDepartmentName string

@description('ASCII key of the pilot department (lower case letters, digits, hyphens), used in the group\'s unique name and mail nickname.')
@minLength(1)
@maxLength(30)
param pilotDepartmentKey string

@description('Accept a claims-mapping policy without a custom signing key on kalla-admin, for the departmentCodes claim (the gateway\'s Entra step 4).')
param acceptMappedClaims bool = true

// The identity running the deployment. It is made an owner of every object it creates, so that with only
// Application.ReadWrite.OwnedBy and Group.Create it can update them on the next run (§12.4).
var deployerObjectId = deployer().objectId

module admin 'admin.bicep' = {
  name: 'kalla-admin'
  params: {
    ragAdminHost: ragAdminHost
    acceptMappedClaims: acceptMappedClaims
    ownerIds: [deployerObjectId]
  }
}

module openWebUi 'open-webui.bicep' = {
  name: 'kalla-open-webui'
  params: {
    openWebUiHost: openWebUiHost
    ownerIds: [deployerObjectId]
  }
}

// Security groups for the pilot (§12.1). Names are agreed with IT; membership is maintained by IT, never here.
var groups = [
  {
    key: 'pilotUsers'
    uniqueName: 'kalla-pilot-users'
    displayName: 'Ume.Rag pilot users'
    description: 'Ume.Rag: may sign in to the pilot Open WebUI.'
  }
  {
    key: 'platformAdmins'
    uniqueName: 'kalla-platform-admins'
    displayName: 'Ume.Rag platform admins'
    description: 'Ume.Rag: app role rag-platform-admin on kalla-admin.'
  }
  {
    key: 'departmentAdmins'
    uniqueName: 'kalla-department-admins-${pilotDepartmentKey}'
    displayName: 'Ume.Rag department admins – ${pilotDepartmentName}'
    description: 'Ume.Rag: app role rag-department-admin on kalla-admin, for ${pilotDepartmentName}.'
  }
]

resource pilotGroups 'Microsoft.Graph/groups@v1.0' = [
  for group in groups: {
    uniqueName: group.uniqueName
    displayName: group.displayName
    description: group.description
    mailEnabled: false
    mailNickname: group.uniqueName
    securityEnabled: true
    // A group created with Group.Create and no owner cannot be modified afterwards (Graph "Create group").
    owners: {
      relationships: [deployerObjectId]
    }
  }
]

output adminAppId string = admin.outputs.appId
output adminServicePrincipalId string = admin.outputs.servicePrincipalId
output adminAppRoles array = admin.outputs.appRoles
output openWebUiAppId string = openWebUi.outputs.appId
output openWebUiServicePrincipalId string = openWebUi.outputs.servicePrincipalId
output groups array = [
  for (group, i) in groups: { key: group.key, uniqueName: group.uniqueName, id: pilotGroups[i].id }
]
