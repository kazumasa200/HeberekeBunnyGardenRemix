/* Print sizes and offsets of remix_c.h (Remix Plus API 1.0.0) structs: expected values for the C# mirror self-check. */
#include <stdio.h>
#include <stddef.h>
#include "../../include/remix_c.h"

#define SZ(T) printf("%-44s %4zu\n", #T, sizeof(T))
#define OF(T, F) printf("  %-42s %4zu\n", #F, offsetof(T, F))
#define SLOT(F) printf("slot %-37s %4zu\n", #F, offsetof(remixapi_Interface, F) / sizeof(void*))

int main(void) {
    SZ(remixapi_HardcodedVertex);
    SZ(remixapi_MeshInfoSkinning);
    SZ(remixapi_MeshInfoSurfaceTriangles);
    OF(remixapi_MeshInfoSurfaceTriangles, skinning_value);
    OF(remixapi_MeshInfoSurfaceTriangles, material);
    SZ(remixapi_MeshInfo);
    SZ(remixapi_InstanceInfo);
    OF(remixapi_InstanceInfo, transform);
    OF(remixapi_InstanceInfo, doubleSided);
    SZ(remixapi_MaterialInfo);
    OF(remixapi_MaterialInfo, emissiveColorConstant);
    OF(remixapi_MaterialInfo, spriteSheetRow);
    SZ(remixapi_MaterialInfoOpaqueEXT);
    OF(remixapi_MaterialInfoOpaqueEXT, albedoConstant);
    OF(remixapi_MaterialInfoOpaqueEXT, heightTexture);
    OF(remixapi_MaterialInfoOpaqueEXT, alphaReferenceValue);
    OF(remixapi_MaterialInfoOpaqueEXT, displaceOut);
    SZ(remixapi_CameraInfo);
    OF(remixapi_CameraInfo, view);
    SZ(remixapi_CameraInfoParameterizedEXT);
    SZ(remixapi_LightInfo);
    OF(remixapi_LightInfo, radiance);
    OF(remixapi_LightInfo, isDynamic);
    SZ(remixapi_LightInfoDistantEXT);
    OF(remixapi_LightInfoDistantEXT, angularDiameterDegrees);
    SZ(remixapi_MaterialInfoOpaqueAlbedoTintEXT);
    SZ(remixapi_RetainedInstanceUpdate);
    SZ(remixapi_LightInfoDomeEXT);
    OF(remixapi_LightInfoDomeEXT, colorTexture);
    SZ(remixapi_MaterialInfoTranslucentEXT);
    OF(remixapi_MaterialInfoTranslucentEXT, transmittanceColor);
    OF(remixapi_MaterialInfoTranslucentEXT, useDiffuseLayer);
    OF(remixapi_RetainedInstanceUpdate, flags);
    SZ(remixapi_LightInfoLightShaping);
    SZ(remixapi_LightInfoSphereEXT);
    OF(remixapi_LightInfoSphereEXT, radius);
    OF(remixapi_LightInfoSphereEXT, shaping_value);
    OF(remixapi_LightInfoSphereEXT, volumetricRadianceScale);
    SZ(remixapi_LightInfoRectEXT);
    OF(remixapi_LightInfoRectEXT, xSize);
    OF(remixapi_LightInfoRectEXT, ySize);
    OF(remixapi_LightInfoRectEXT, direction);
    OF(remixapi_LightInfoRectEXT, shaping_value);
    OF(remixapi_LightInfoRectEXT, volumetricRadianceScale);
    SZ(remixapi_InitializeLibraryInfo);
    SZ(remixapi_Interface);
    SLOT(Shutdown); SLOT(CreateMaterial); SLOT(DestroyMaterial); SLOT(CreateMesh); SLOT(DestroyMesh);
    SLOT(SetupCamera); SLOT(DrawInstance); SLOT(CreateLight); SLOT(DestroyLight); SLOT(DrawLightInstance);
    SLOT(SetConfigVariable); SLOT(DestroyTexture);
    printf("REMIXAPI_VERSION %llu\n", (unsigned long long)REMIXAPI_VERSION_MAKE(REMIXAPI_VERSION_MAJOR, REMIXAPI_VERSION_MINOR, REMIXAPI_VERSION_PATCH));
    printf("StructType InitializeLibraryInfo=%d MaterialInfo=%d MaterialInfoOpaqueExt=%d MeshInfo=%d InstanceInfo=%d\n",
        REMIXAPI_STRUCT_TYPE_INITIALIZE_LIBRARY_INFO, REMIXAPI_STRUCT_TYPE_MATERIAL_INFO, REMIXAPI_STRUCT_TYPE_MATERIAL_INFO_OPAQUE_EXT,
        REMIXAPI_STRUCT_TYPE_MESH_INFO, REMIXAPI_STRUCT_TYPE_INSTANCE_INFO);
    printf("StructType CameraInfo=%d CameraInfoParameterizedExt=%d LightInfo=%d LightInfoDistantExt=%d\n",
        REMIXAPI_STRUCT_TYPE_CAMERA_INFO, REMIXAPI_STRUCT_TYPE_CAMERA_INFO_PARAMETERIZED_EXT,
        REMIXAPI_STRUCT_TYPE_LIGHT_INFO, REMIXAPI_STRUCT_TYPE_LIGHT_INFO_DISTANT_EXT);
    return 0;
}
