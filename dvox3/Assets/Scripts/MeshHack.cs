using System.Collections;
using System.Collections.Generic;
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
}
