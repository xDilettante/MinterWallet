//go:build tools

// Keeps the requirements of the node binary (go build github.com/MinterTeam/minter-go-node/cmd/minter) in go.mod.
package main

import _ "github.com/MinterTeam/minter-go-node/cmd/minter/cmd"
