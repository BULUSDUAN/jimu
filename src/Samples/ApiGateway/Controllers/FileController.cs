﻿using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ApiGateway.Controllers
{
    //[Authorize]
    public class FileController : Controller
    {

        public IActionResult Index()
        {
            return View();
        }

        public IActionResult Hi()
        {
            return View();
        }
    }
}
