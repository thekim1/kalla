// Sample parameters with no real values. Copy to test.bicepparam / prod.bicepparam (the names entra.yml applies for the
// environments entra-test / entra-prod) and fill in the real host names and the pilot department.
using 'main.bicep'

param ragAdminHost = 'kalla-admin.example.invalid'
param openWebUiHost = 'chat.example.invalid'
param pilotDepartmentName = 'Exempelnämnden'
param pilotDepartmentKey = 'exempel'
