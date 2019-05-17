using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class MeshHack : MonoBehaviour
{
    public Mesh cubeMesh;
    public Material cubeMaterial;

    public static Mesh CubeMesh;
    public static Material CubeMaterial;
    
    // Start is called before the first frame update
    void Start()
    {
        CubeMesh = cubeMesh;
        CubeMaterial = cubeMaterial;
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    [MenuItem("Window/SPAWN CUBE")]
    public static void SpawnMesh()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.GetComponent<MeshFilter>().mesh = ChunkBuildMeshJS.GetCubeMesh();
    }
}
